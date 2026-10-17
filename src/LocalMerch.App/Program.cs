using System.Globalization;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using LocalMerch.Core;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.EntityFrameworkCore;

namespace LocalMerch.App;

public static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        var headless = args.Contains("--no-ui");
        args = args.Where(a => a != "--no-ui").ToArray();
        var dataDir = Environment.GetEnvironmentVariable("LOCALMERCH_DATA")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LocalMerch");
        Directory.CreateDirectory(dataDir);

        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args,
            ContentRootPath = AppContext.BaseDirectory,
        });
        var culture = CultureInfo.GetCultureInfo(builder.Configuration["Culture"] ?? "fr-FR");
        CultureInfo.DefaultThreadCurrentCulture = CultureInfo.DefaultThreadCurrentUICulture = culture;

        builder.Services.AddDbContext<LocalMerchDb>(o =>
            o.UseSqlite($"Data Source={Path.Combine(dataDir, "localmerch.db")}"));
        builder.Services.AddScoped<Shop>();
        builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme).AddCookie(o =>
        {
            o.Cookie.SameSite = SameSiteMode.Strict;
            o.ExpireTimeSpan = TimeSpan.FromHours(8);
            o.Events.OnRedirectToLogin = c => { c.Response.StatusCode = 401; return Task.CompletedTask; };
            o.Events.OnRedirectToAccessDenied = c => { c.Response.StatusCode = 403; return Task.CompletedTask; };
            // Droits relus en base à chaque requête : un compte désactivé ou supprimé perd sa session,
            // un changement de rôle ou de droits s'applique immédiatement.
            o.Events.OnValidatePrincipal = async c =>
            {
                var db = c.HttpContext.RequestServices.GetRequiredService<LocalMerchDb>();
                var user = int.TryParse(c.Principal?.FindFirstValue(ClaimTypes.NameIdentifier), out var id)
                    ? await db.Users.FindAsync(id) : null;
                // Le login est comparé aussi : après une restauration, un même Id peut désigner une autre personne.
                if (user is not { Active: true } || user.Login != c.Principal!.FindFirstValue(Policies.LoginClaim))
                {
                    c.RejectPrincipal();
                    await c.HttpContext.SignOutAsync();
                }
                else c.ReplacePrincipal(Policies.Principal(user));
            };
        });
        Policies.Register(builder.Services.AddAuthorizationBuilder());
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();

        PrepareDatabase(app, dataDir);

        var allowRemote = app.Configuration.GetValue<bool>("Admin:AllowRemote");
        app.Use(async (ctx, next) =>
        {
            if (!allowRemote && ctx.Connection.RemoteIpAddress is { } ip && !LocalNetwork.Contains(ip))
            {
                ctx.Response.StatusCode = 403;
                return;
            }
            await next();
        });
        app.UseDefaultFiles();
        app.UseStaticFiles();
        app.UseAuthentication();
        app.UseAuthorization();
        AdminApi.Map(app, dataDir);

        if (headless)
        {
            app.Run();
            return;
        }

        app.Start();
        PosApp.Run(args, app.Services, app.Configuration.GetSection("Pos").Get<PosOptions>() ?? new());
        app.StopAsync().GetAwaiter().GetResult();
    }

    /// <summary>Met la base à jour et garantit un compte administrateur. Au démarrage et après une restauration.</summary>
    internal static void PrepareDatabase(WebApplication app, string dataDir)
    {
        using var scope = app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<LocalMerchDb>();
        db.Database.Migrate();
        if (db.Users.Any(u => u.Role == Role.Admin && u.Active && u.PasswordHash != null)) return;

        var admin = db.Users.FirstOrDefault(u => u.Login == "admin") ?? db.Users.Add(new User { Login = "admin" }).Entity;
        admin.DisplayName = "Administrateur";
        admin.Role = Role.Admin;
        admin.Active = true;
        admin.PasswordHash = Secret.Hash(InitialPassword(app.Configuration, dataDir, app.Logger));
        db.SaveChanges();
    }

    /// <summary>Mot de passe du compte « admin » créé au premier démarrage : « Admin:Password »,
    /// sinon généré et gardé dans le dossier de données.</summary>
    static string InitialPassword(IConfiguration config, string dataDir, ILogger log)
    {
        if (config["Admin:Password"] is { Length: > 0 } configured) return configured;

        var file = Path.Combine(dataDir, "admin-password.txt");
        if (!File.Exists(file))
        {
            File.WriteAllText(file, RandomNumberGenerator.GetHexString(16, lowercase: true));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        log.LogWarning("Compte « admin » créé, mot de passe dans {File}", file);
        return File.ReadAllText(file).Trim();
    }
}

public record PosOptions
{
    public int Rows { get; init; } = 6;
    public int Columns { get; init; } = 5;
    public bool FullScreen { get; init; } = true;
    /// <summary>TVA appliquée aux montants libres (non demandée à la caisse).</summary>
    public decimal FreeAmountVatRate { get; init; } = 20m;
}
