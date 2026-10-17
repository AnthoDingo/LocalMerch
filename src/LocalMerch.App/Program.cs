using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using LocalMerch.Core;
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
        });
        builder.Services.AddAuthorization();
        builder.Services.ConfigureHttpJsonOptions(o => o.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        var app = builder.Build();

        using (var scope = app.Services.CreateScope())
            scope.ServiceProvider.GetRequiredService<LocalMerchDb>().Database.Migrate();

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
        AdminApi.Map(app, AdminPassword(app.Configuration, dataDir, app.Logger));

        if (headless)
        {
            app.Run();
            return;
        }

        app.Start();
        PosApp.Run(args, app.Services, app.Configuration.GetSection("Pos").Get<PosOptions>() ?? new());
        app.StopAsync().GetAwaiter().GetResult();
    }

    /// <summary>Mot de passe « Admin:Password », sinon généré une fois et gardé dans le dossier de données.</summary>
    static string AdminPassword(IConfiguration config, string dataDir, ILogger log)
    {
        if (config["Admin:Password"] is { Length: > 0 } configured) return configured;

        var file = Path.Combine(dataDir, "admin-password.txt");
        if (!File.Exists(file))
        {
            File.WriteAllText(file, RandomNumberGenerator.GetHexString(16, lowercase: true));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(file, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
        log.LogWarning("Mot de passe d'administration : voir {File}", file);
        return File.ReadAllText(file).Trim();
    }
}

public record PosOptions
{
    public int Rows { get; init; } = 5;
    public int Columns { get; init; } = 6;
    public bool FullScreen { get; init; } = true;
}
