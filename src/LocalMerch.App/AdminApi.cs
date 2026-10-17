using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using System.Security.Claims;
using Microsoft.AspNetCore.Http.Features;
using LocalMerch.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace LocalMerch.App;

/// <summary>
/// Autorisations : « Admin » pour les pages d'administration, « Page:Niveau » (ex. « Products:Write ») pour les
/// pages métier. Les droits sont portés par des claims, reconstruites depuis la base à chaque requête.
/// </summary>
public static class Policies
{
    public const string Admin = nameof(Admin);
    public const string LoginClaim = "login";
    const string AccessClaim = "access:";

    public static string For(Page page, Access access) => $"{page}:{access}";

    public static void Register(AuthorizationBuilder auth)
    {
        auth.AddPolicy(Admin, p => p.RequireRole(nameof(Role.Admin)));
        foreach (var page in Enum.GetValues<Page>())
            foreach (var access in (Access[])[Access.Read, Access.Write])
                auth.AddPolicy(For(page, access), p => p.RequireAssertion(c => Allows(c.User, page, access)));
    }

    public static bool Allows(ClaimsPrincipal user, Page page, Access needed) =>
        Enum.TryParse<Access>(user.FindFirstValue(AccessClaim + page), out var access) && access >= needed;

    public static ClaimsPrincipal Principal(User user) => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.NameIdentifier, user.Id.ToString()),
        new Claim(LoginClaim, user.Login),
        new Claim(ClaimTypes.Name, user.DisplayName),
        new Claim(ClaimTypes.Role, user.Role.ToString()),
        .. Enum.GetValues<Page>().Select(p => new Claim(AccessClaim + p, user.AccessTo(p).ToString())),
    ], CookieAuthenticationDefaults.AuthenticationScheme));
}

/// <summary>API JSON de la page d'administration (wwwroot/index.html).</summary>
public static class AdminApi
{
    record Login(string? Username, string? Password);
    record NewMovement(int ProductId, MovementType Type, int Quantity, string? Reason);
    record UserInput(string? Login, string? DisplayName, Role Role, bool Active, string? Password, List<PagePermission>? Permissions);
    record BrandingInput(string? Title);
    record PasswordChange(string? Current, string? Next);

    const int MaxLogoBytes = 512 * 1024;
    const long MaxBackupBytes = 500L * 1024 * 1024;
    static readonly string[] LogoTypes = ["image/png", "image/jpeg", "image/webp", "image/gif"];

    public static void Map(WebApplication app, string dataDir)
    {
        app.MapPost("/api/login", async (Login req, HttpContext http, LocalMerchDb db) =>
        {
            var login = req.Username?.Trim().ToLowerInvariant();
            var user = await db.Users.FirstOrDefaultAsync(u => u.Login == login && u.Active);
            if (user is null || !Secret.Verify(req.Password ?? "", user.PasswordHash))
            {
                await Task.Delay(1000); // freine les essais en rafale
                return Results.Unauthorized();
            }

            await http.SignInAsync(Policies.Principal(user));
            return Results.NoContent();
        });
        app.MapPost("/api/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync();
            return Results.NoContent();
        });

        // Public : affiché sur l'écran de connexion.
        app.MapGet("/api/branding", async (LocalMerchDb db) => await db.Settings.FindAsync(1) ?? new ShopSettings());
        app.MapGet("/logo", async (LocalMerchDb db, HttpContext http) =>
        {
            http.Response.Headers.CacheControl = "no-cache";
            http.Response.Headers.XContentTypeOptions = "nosniff";
            return await db.Settings.FindAsync(1) is { Logo: { } logo, LogoContentType: { } type }
                ? Results.File(logo, type)
                : Results.NotFound();
        });

        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (ShopException e) { return Results.BadRequest(new { error = e.Message }); }
        });
        var admin = api.MapGroup("").RequireAuthorization(Policies.Admin);
        static string Read(Page page) => Policies.For(page, Access.Read);
        static string Write(Page page) => Policies.For(page, Access.Write);

        api.MapGet("/me", (ClaimsPrincipal me) => new
        {
            name = me.Identity?.Name,
            login = me.FindFirstValue(Policies.LoginClaim),
            role = me.FindFirstValue(ClaimTypes.Role),
            access = Enum.GetValues<Page>().ToDictionary(p => p.ToString().ToLowerInvariant(),
                p => Policies.Allows(me, p, Access.Write) ? Access.Write : Policies.Allows(me, p, Access.Read) ? Access.Read : Access.None),
        });
        // Tout utilisateur connecté peut changer son propre mot de passe, en donnant l'actuel.
        api.MapPut("/me/password", async (PasswordChange req, ClaimsPrincipal me, LocalMerchDb db) =>
        {
            var user = await db.Users.FindAsync(int.Parse(me.FindFirstValue(ClaimTypes.NameIdentifier)!));
            if (user is null) return Results.Unauthorized();
            if (!Secret.Verify(req.Current ?? "", user.PasswordHash))
            {
                await Task.Delay(1000); // freine les essais en rafale
                return Refused("Mot de passe actuel incorrect.")!;
            }
            if (req.Next is not { Length: >= 8 } next) return Refused("Le nouveau mot de passe doit faire au moins 8 caractères.")!;
            user.PasswordHash = Secret.Hash(next);
            return await Save(db) ?? Results.NoContent();
        });

        // Noms seuls, pour les listes déroulantes et les tableaux de toutes les pages.
        api.MapGet("/lookups", async (LocalMerchDb db) => new
        {
            products = await db.Products.AsNoTracking().OrderBy(p => p.Name).Select(p => new { p.Id, p.Name }).ToListAsync(),
            suppliers = await db.Suppliers.AsNoTracking().OrderBy(s => s.Name).Select(s => new { s.Id, s.Name }).ToListAsync(),
        });
        var pos = app.Configuration.GetSection("Pos").Get<PosOptions>() ?? new();
        api.MapGet("/pos/layout", () => new { pos.Rows, pos.Columns }).RequireAuthorization(Read(Page.Keys));

        Crud(api, "products", Page.Products, db => db.Products);
        Crud(api, "keys", Page.Keys, db => db.PosKeys);
        Crud(api, "suppliers", Page.Suppliers, db => db.Suppliers);
        Crud(api, "quotes", Page.Quotes, db => db.Quotes, (q, old) =>
        {
            q.CreatedAt = old?.CreatedAt ?? DateTime.UtcNow;
            return q.Status == QuoteStatus.Accepted && old?.Status != QuoteStatus.Accepted
                ? "Utilisez « Convertir en commande » pour accepter un devis." : null;
        });
        Crud(api, "orders", Page.Orders, db => db.PurchaseOrders, (o, old) =>
        {
            if (old?.Status == OrderStatus.Received) return "Commande déjà réceptionnée : elle n'est plus modifiable.";
            if (o.Status == OrderStatus.Received) return "Utilisez « Réceptionner » pour entrer la commande en stock.";
            o.CreatedAt = old?.CreatedAt ?? DateTime.UtcNow;
            o.QuoteId = old?.QuoteId;
            o.ReceivedAt = null;
            return null;
        });

        // Convertir un devis crée une commande : il faut pouvoir modifier les deux.
        api.MapPost("/quotes/{id:int}/convert", (int id, Shop shop) => shop.ConvertQuote(id))
            .RequireAuthorization(Write(Page.Quotes), Write(Page.Orders));
        api.MapPost("/orders/{id:int}/receive", (int id, Shop shop) => shop.ReceiveOrder(id))
            .RequireAuthorization(Write(Page.Orders));

        api.MapGet("/stock", async (LocalMerchDb db, Shop shop) =>
        {
            var levels = shop.StockLevels();
            var products = await db.Products.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
            return products.Select(p => new { productId = p.Id, p.Name, p.Active, quantity = levels.GetValueOrDefault(p.Id) });
        }).RequireAuthorization(Read(Page.Stock));
        api.MapGet("/stock/movements", (LocalMerchDb db, int? productId) =>
            db.StockMovements.AsNoTracking()
                .Where(m => productId == null || m.ProductId == productId)
                .OrderByDescending(m => m.Id).Take(200).ToListAsync())
            .RequireAuthorization(Read(Page.Stock));
        api.MapPost("/stock/movements", (NewMovement m, Shop shop) =>
            shop.RecordMovement(m.ProductId, m.Type, m.Quantity, m.Reason))
            .RequireAuthorization(Write(Page.Stock));

        admin.MapGet("/terminal", async (LocalMerchDb db) => await db.PaymentTerminals.FindAsync(1) ?? new PaymentTerminal());
        admin.MapPut("/terminal", async (PaymentTerminal t, LocalMerchDb db) =>
        {
            t.Id = 1;
            if (Invalid(t) is { } error) return error;
            if (await db.PaymentTerminals.FindAsync(1) is { } existing) db.Entry(existing).CurrentValues.SetValues(t);
            else db.PaymentTerminals.Add(t);
            return await Save(db) ?? Results.Ok(t);
        });
        // Vérifie seulement que le TPE accepte une connexion TCP, avec la configuration enregistrée.
        admin.MapPost("/terminal/test", async (LocalMerchDb db) =>
        {
            if (await db.PaymentTerminals.FindAsync(1) is not { Host: { Length: > 0 } host, Port: { } port })
                return Results.BadRequest(new { error = "Enregistrez d'abord l'adresse et le port du TPE." });
            using var tcp = new TcpClient();
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try
            {
                await tcp.ConnectAsync(host, port, cts.Token);
                return Results.Ok(new { message = $"TPE joignable sur {host}:{port}." });
            }
            catch (Exception e) when (e is SocketException or OperationCanceledException)
            {
                return Results.BadRequest(new { error = $"TPE injoignable sur {host}:{port} ({(e is OperationCanceledException ? "délai dépassé" : e.Message)})." });
            }
        });

        api.MapGet("/sales", (LocalMerchDb db) =>
            db.Sales.AsNoTracking().OrderByDescending(s => s.Id).Take(200).ToListAsync())
            .RequireAuthorization(Read(Page.Sales));
        api.MapPost("/sales/{id:int}/refund", (int id, Shop shop) => shop.RefundSale(id))
            .RequireAuthorization(Write(Page.Sales));

        admin.MapGet("/users", (LocalMerchDb db) => db.Users.AsNoTracking().OrderBy(u => u.DisplayName).ToListAsync());
        admin.MapPost("/users", (UserInput input, LocalMerchDb db) => SaveUser(new User(), input, db));
        admin.MapPut("/users/{id:int}", async (int id, UserInput input, LocalMerchDb db) =>
            await db.Users.FindAsync(id) is { } user ? await SaveUser(user, input, db) : Results.NotFound());
        admin.MapDelete("/users/{id:int}", async (int id, LocalMerchDb db, ClaimsPrincipal me) =>
        {
            if (await db.Users.FindAsync(id) is not { } user) return Results.NotFound();
            if (id.ToString() == me.FindFirstValue(ClaimTypes.NameIdentifier))
                return Refused("Vous ne pouvez pas supprimer votre propre compte.")!;
            if (IsLastAdmin(user, db)) return Refused("Il doit rester au moins un administrateur actif.")!;
            db.Users.Remove(user);
            return await Save(db) ?? Results.NoContent();
        });

        admin.MapPut("/branding", async (BrandingInput input, LocalMerchDb db) =>
        {
            var settings = await Settings(db);
            settings.Title = input.Title?.Trim() ?? "";
            if (Invalid(settings) is { } error) return error;
            return await Save(db) ?? Results.Ok(settings);
        });
        // Corps brut (Content-Type = type de l'image) : pas de formulaire, donc pas de CSRF possible sans pré-vol CORS.
        admin.MapPost("/branding/logo", async (HttpRequest req, LocalMerchDb db) =>
        {
            var type = req.ContentType?.Split(';')[0].Trim().ToLowerInvariant();
            if (type is null || !LogoTypes.Contains(type)) return Refused("Format accepté : PNG, JPEG, WebP ou GIF.")!;
            if (req.ContentLength > MaxLogoBytes) return Refused("Logo trop lourd (512 Ko maximum).")!;

            using var buffer = new MemoryStream();
            await req.Body.CopyToAsync(buffer);
            if (buffer.Length is 0 or > MaxLogoBytes) return Refused("Logo vide ou trop lourd (512 Ko maximum).")!;

            var settings = await Settings(db);
            settings.Logo = buffer.ToArray();
            settings.LogoContentType = type;
            return await Save(db) ?? Results.NoContent();
        });
        var databasePath = Path.Combine(dataDir, "localmerch.db");
        admin.MapGet("/backup", () =>
        {
            var file = Path.Combine(Path.GetTempPath(), $"localmerch-{Guid.NewGuid():N}.db");
            Backup.Save(databasePath, file);
            var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.None, 81920, FileOptions.DeleteOnClose);
            return Results.File(stream, "application/vnd.sqlite3", $"localmerch-{DateTime.Now:yyyyMMdd-HHmm}.db");
        });
        // Corps brut (application/octet-stream) : comme pour le logo, pas de CSRF possible sans pré-vol CORS.
        admin.MapPost("/restore", async (HttpRequest req, LocalMerchDb db) =>
        {
            if (req.HttpContext.Features.Get<IHttpMaxRequestBodySizeFeature>() is { IsReadOnly: false } limit)
                limit.MaxRequestBodySize = MaxBackupBytes;
            var upload = Path.Combine(Path.GetTempPath(), $"localmerch-restore-{Guid.NewGuid():N}.db");
            try
            {
                await using (var file = File.Create(upload)) await req.Body.CopyToAsync(file);
                Backup.Validate(upload, db.Database.GetMigrations());

                var safety = Path.Combine(dataDir, "backups", $"avant-restauration-{DateTime.Now:yyyyMMdd-HHmmss}.db");
                Directory.CreateDirectory(Path.GetDirectoryName(safety)!);
                Backup.Save(databasePath, safety);

                Backup.Restore(upload, databasePath);
                Program.PrepareDatabase(app, dataDir); // sauvegarde ancienne : migrations ; sans admin : compte recréé
                LocalMerchDb.NotifyCatalogChanged();
                return Results.Ok(new { message = $"Restauration terminée. L'ancienne base a été conservée : {safety}" });
            }
            finally
            {
                File.Delete(upload);
            }
        });

        admin.MapDelete("/branding/logo", async (LocalMerchDb db) =>
        {
            var settings = await Settings(db);
            settings.Logo = null;
            settings.LogoContentType = null;
            return await Save(db) ?? Results.NoContent();
        });
    }

    /// <param name="check">Appelé avant création (old = null) ou modification : corrige les champs gérés
    /// par le serveur et renvoie un message d'erreur pour refuser.</param>
    static void Crud<T>(RouteGroupBuilder api, string name, Page page, Func<LocalMerchDb, DbSet<T>> set,
        Func<T, T?, string?>? check = null) where T : class, IEntity
    {
        var g = api.MapGroup("/" + name);
        var read = Policies.For(page, Access.Read);
        var write = Policies.For(page, Access.Write);

        g.MapGet("/", (LocalMerchDb db) => set(db).AsNoTracking().OrderBy(e => e.Id).ToListAsync())
            .RequireAuthorization(read);

        g.MapGet("/{id:int}", async (int id, LocalMerchDb db) =>
            await set(db).FindAsync(id) is { } e ? Results.Ok(e) : Results.NotFound())
            .RequireAuthorization(read);

        g.MapPost("/", async (T e, LocalMerchDb db) =>
        {
            e.Id = 0;
            if ((Invalid(e) ?? Refused(check?.Invoke(e, null))) is { } error) return error;
            if (e is IHasLines doc) doc.Lines.ForEach(l => l.Id = 0);
            set(db).Add(e);
            return await Save(db) ?? Results.Created($"/api/{name}/{e.Id}", e);
        }).RequireAuthorization(write);

        g.MapPut("/{id:int}", async (int id, T e, LocalMerchDb db) =>
        {
            var existing = await set(db).FindAsync(id);
            if (existing is null) return Results.NotFound();
            e.Id = id;
            if ((Invalid(e) ?? Refused(check?.Invoke(e, existing))) is { } error) return error;

            db.Entry(existing).CurrentValues.SetValues(e);
            if (existing is IHasLines target && e is IHasLines source)
            {
                target.Lines.Clear();
                target.Lines.AddRange(source.Lines.Select(l => new DocumentLine
                {
                    ProductId = l.ProductId, Quantity = l.Quantity, UnitPrice = l.UnitPrice,
                }));
            }
            return await Save(db) ?? Results.Ok(existing);
        }).RequireAuthorization(write);

        g.MapDelete("/{id:int}", async (int id, LocalMerchDb db) =>
        {
            if (await set(db).FindAsync(id) is not { } e) return Results.NotFound();
            set(db).Remove(e);
            return await Save(db) ?? Results.NoContent();
        }).RequireAuthorization(write);
    }

    static async Task<ShopSettings> Settings(LocalMerchDb db)
    {
        if (await db.Settings.FindAsync(1) is { } settings) return settings;
        settings = new ShopSettings();
        db.Settings.Add(settings);
        return settings;
    }

    static bool IsLastAdmin(User user, LocalMerchDb db) =>
        user is { Role: Role.Admin, Active: true } && !db.Users.Any(u => u.Id != user.Id && u.Role == Role.Admin && u.Active);

    /// <summary>Mot de passe vide = inchangé.</summary>
    static async Task<IResult> SaveUser(User user, UserInput input, LocalMerchDb db)
    {
        var wasLastAdmin = user.Id != 0 && IsLastAdmin(user, db);
        user.Login = input.Login?.Trim().ToLowerInvariant() ?? "";
        user.DisplayName = input.DisplayName?.Trim() ?? "";
        user.Role = input.Role;
        user.Active = input.Active;
        user.Permissions = (input.Permissions ?? [])
            .Where(p => p.Access != Access.None && Enum.IsDefined(p.Page) && Enum.IsDefined(p.Access))
            .DistinctBy(p => p.Page)
            .Select(p => new PagePermission { Page = p.Page, Access = p.Access })
            .ToList();
        if (input.Password is { Length: > 0 } password)
        {
            if (password.Length < 8) return Refused("Le mot de passe doit faire au moins 8 caractères.")!;
            user.PasswordHash = Secret.Hash(password);
        }
        if (Invalid(user) is { } error) return error;
        if (wasLastAdmin && user is not { Role: Role.Admin, Active: true })
            return Refused("Il doit rester au moins un administrateur actif.")!;

        if (user.Id == 0) db.Users.Add(user);
        return await Save(db) ?? Results.Ok(user);
    }

    static async Task<IResult?> Save(LocalMerchDb db)
    {
        try
        {
            await db.SaveChangesAsync();
            return null;
        }
        catch (DbUpdateException)
        {
            return Results.Conflict(new { error = "Enregistrement impossible : élément utilisé ailleurs, inconnu ou en double." });
        }
    }

    static IResult? Refused(string? message) => message is null ? null : Results.BadRequest(new { error = message });

    static IResult? Invalid(object e)
    {
        var errors = new List<ValidationResult>();
        Validator.TryValidateObject(e, new ValidationContext(e), errors, validateAllProperties: true);
        if (e is IHasLines doc)
            foreach (var line in doc.Lines)
                Validator.TryValidateObject(line, new ValidationContext(line), errors, validateAllProperties: true);

        return errors.Count == 0 ? null : Results.ValidationProblem(errors
            .GroupBy(r => r.MemberNames.FirstOrDefault() ?? "")
            .ToDictionary(g => g.Key, g => g.Select(r => r.ErrorMessage ?? "Invalide").ToArray()));
    }
}
