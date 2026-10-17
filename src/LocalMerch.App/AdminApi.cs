using System.ComponentModel.DataAnnotations;
using System.Net.Sockets;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using LocalMerch.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace LocalMerch.App;

/// <summary>API JSON de la page d'administration (wwwroot/index.html).</summary>
public static class AdminApi
{
    record Login(string Password);
    record NewMovement(int ProductId, MovementType Type, int Quantity, string? Reason);

    public static void Map(WebApplication app, string password)
    {
        var expected = Encoding.UTF8.GetBytes(password);
        app.MapPost("/api/login", async (Login req, HttpContext http) =>
        {
            if (!CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(req.Password ?? ""), expected))
            {
                await Task.Delay(1000); // freine les essais en rafale
                return Results.Unauthorized();
            }
            var identity = new ClaimsIdentity([new Claim(ClaimTypes.Name, "admin")], CookieAuthenticationDefaults.AuthenticationScheme);
            await http.SignInAsync(new ClaimsPrincipal(identity));
            return Results.NoContent();
        });
        app.MapPost("/api/logout", async (HttpContext http) =>
        {
            await http.SignOutAsync();
            return Results.NoContent();
        });

        var api = app.MapGroup("/api").RequireAuthorization().AddEndpointFilter(async (ctx, next) =>
        {
            try { return await next(ctx); }
            catch (ShopException e) { return Results.BadRequest(new { error = e.Message }); }
        });

        api.MapGet("/me", () => Results.NoContent());
        var pos = app.Configuration.GetSection("Pos").Get<PosOptions>() ?? new();
        api.MapGet("/pos/layout", () => new { pos.Rows, pos.Columns });

        Crud(api, "products", db => db.Products);
        Crud(api, "keys", db => db.PosKeys);
        Crud(api, "suppliers", db => db.Suppliers);
        Crud(api, "quotes", db => db.Quotes, (q, old) =>
        {
            q.CreatedAt = old?.CreatedAt ?? DateTime.UtcNow;
            return q.Status == QuoteStatus.Accepted && old?.Status != QuoteStatus.Accepted
                ? "Utilisez « Convertir en commande » pour accepter un devis." : null;
        });
        Crud(api, "orders", db => db.PurchaseOrders, (o, old) =>
        {
            if (old?.Status == OrderStatus.Received) return "Commande déjà réceptionnée : elle n'est plus modifiable.";
            if (o.Status == OrderStatus.Received) return "Utilisez « Réceptionner » pour entrer la commande en stock.";
            o.CreatedAt = old?.CreatedAt ?? DateTime.UtcNow;
            o.QuoteId = old?.QuoteId;
            o.ReceivedAt = null;
            return null;
        });

        api.MapPost("/quotes/{id:int}/convert", (int id, Shop shop) => shop.ConvertQuote(id));
        api.MapPost("/orders/{id:int}/receive", (int id, Shop shop) => shop.ReceiveOrder(id));

        api.MapGet("/stock", async (LocalMerchDb db, Shop shop) =>
        {
            var levels = shop.StockLevels();
            var products = await db.Products.AsNoTracking().OrderBy(p => p.Name).ToListAsync();
            return products.Select(p => new { productId = p.Id, p.Name, p.Active, quantity = levels.GetValueOrDefault(p.Id) });
        });
        api.MapGet("/stock/movements", (LocalMerchDb db, int? productId) =>
            db.StockMovements.AsNoTracking()
                .Where(m => productId == null || m.ProductId == productId)
                .OrderByDescending(m => m.Id).Take(200).ToListAsync());
        api.MapPost("/stock/movements", (NewMovement m, Shop shop) =>
            shop.RecordMovement(m.ProductId, m.Type, m.Quantity, m.Reason));

        api.MapGet("/terminal", async (LocalMerchDb db) => await db.PaymentTerminals.FindAsync(1) ?? new PaymentTerminal());
        api.MapPut("/terminal", async (PaymentTerminal t, LocalMerchDb db) =>
        {
            t.Id = 1;
            if (Invalid(t) is { } error) return error;
            if (await db.PaymentTerminals.FindAsync(1) is { } existing) db.Entry(existing).CurrentValues.SetValues(t);
            else db.PaymentTerminals.Add(t);
            return await Save(db) ?? Results.Ok(t);
        });
        // Vérifie seulement que le TPE accepte une connexion TCP, avec la configuration enregistrée.
        api.MapPost("/terminal/test", async (LocalMerchDb db) =>
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
            db.Sales.AsNoTracking().OrderByDescending(s => s.Id).Take(200).ToListAsync());
        api.MapPost("/sales/{id:int}/refund", (int id, Shop shop) => shop.RefundSale(id));
    }

    /// <param name="check">Appelé avant création (old = null) ou modification : corrige les champs gérés
    /// par le serveur et renvoie un message d'erreur pour refuser.</param>
    static void Crud<T>(RouteGroupBuilder api, string name, Func<LocalMerchDb, DbSet<T>> set,
        Func<T, T?, string?>? check = null) where T : class, IEntity
    {
        var g = api.MapGroup("/" + name);

        g.MapGet("/", (LocalMerchDb db) => set(db).AsNoTracking().OrderBy(e => e.Id).ToListAsync());

        g.MapGet("/{id:int}", async (int id, LocalMerchDb db) =>
            await set(db).FindAsync(id) is { } e ? Results.Ok(e) : Results.NotFound());

        g.MapPost("/", async (T e, LocalMerchDb db) =>
        {
            e.Id = 0;
            if ((Invalid(e) ?? Refused(check?.Invoke(e, null))) is { } error) return error;
            if (e is IHasLines doc) doc.Lines.ForEach(l => l.Id = 0);
            set(db).Add(e);
            return await Save(db) ?? Results.Created($"/api/{name}/{e.Id}", e);
        });

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
        });

        g.MapDelete("/{id:int}", async (int id, LocalMerchDb db) =>
        {
            if (await set(db).FindAsync(id) is not { } e) return Results.NotFound();
            set(db).Remove(e);
            return await Save(db) ?? Results.NoContent();
        });
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
