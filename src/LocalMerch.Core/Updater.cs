using System.Formats.Tar;
using System.IO.Compression;
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json.Serialization;

namespace LocalMerch.Core;

public record UpdateInfo(string Version, string? Notes, string? PageUrl, string? PackageUrl, string? Sha256);

/// <summary>
/// Mises à jour depuis les releases GitHub (workflow .github/workflows/release.yml) : une archive
/// « LocalMerch-vX.Y.Z-linux-x64.tar.gz » par architecture, contenant un dossier « localmerch/ ».
/// L'installation remplace les fichiers du dossier de l'application ; il faut ensuite redémarrer (systemd s'en charge).
/// </summary>
public class Updater(HttpClient http, string? repository, string installDir, string dataDir)
{
    record Release(
        [property: JsonPropertyName("tag_name")] string TagName,
        [property: JsonPropertyName("body")] string? Body,
        [property: JsonPropertyName("html_url")] string? HtmlUrl,
        [property: JsonPropertyName("assets")] List<Asset> Assets);
    record Asset(
        [property: JsonPropertyName("name")] string Name,
        [property: JsonPropertyName("browser_download_url")] string Url,
        [property: JsonPropertyName("digest")] string? Digest);

    readonly SemaphoreSlim busy = new(1, 1);

    public static string CurrentVersion { get; } = Clean(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");

    /// <summary>Paquet attendu pour cette machine (les releases ne sont publiées que pour Linux).</summary>
    public static string Platform => $"linux-{RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant()}";
    public static bool CanInstall => OperatingSystem.IsLinux();

    public DateTime? LastCheck { get; private set; }
    public UpdateInfo? Available { get; private set; }
    public string? LastError { get; private set; }
    /// <summary>Version installée en attente de redémarrage.</summary>
    public string? Installed { get; private set; }

    public static bool IsNewer(string candidate, string current) =>
        Version.TryParse(Clean(candidate), out var c) && Version.TryParse(Clean(current), out var v) && c > v;

    /// <summary>« v1.2.3-beta+abc » → « 1.2.3 ».</summary>
    static string Clean(string version) => version.TrimStart('v', 'V').Split('+', '-')[0];

    public async Task<UpdateInfo?> CheckAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(repository))
        {
            LastError = "Aucun dépôt de mise à jour configuré (Updates:Repository).";
            return null;
        }
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{repository}/releases/latest");
            request.Headers.UserAgent.ParseAdd("LocalMerch/" + CurrentVersion);
            request.Headers.Accept.ParseAdd("application/vnd.github+json");
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                (Available, LastError, LastCheck) = (null, null, DateTime.UtcNow);
                return null; // aucune release publiée
            }
            response.EnsureSuccessStatusCode();
            var release = await response.Content.ReadFromJsonAsync<Release>(ct)
                ?? throw new InvalidDataException("Réponse vide.");

            var asset = release.Assets.FirstOrDefault(a => a.Name.EndsWith($"-{Platform}.tar.gz", StringComparison.Ordinal));
            Available = IsNewer(release.TagName, CurrentVersion)
                ? new UpdateInfo(Clean(release.TagName), release.Body, release.HtmlUrl, asset?.Url,
                    asset?.Digest is { } d && d.StartsWith("sha256:") ? d[7..] : null)
                : null;
            LastError = null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException or InvalidDataException)
        {
            LastError = "Recherche impossible : " + e.Message;
        }
        LastCheck = DateTime.UtcNow;
        return Available;
    }

    /// <summary>Sauvegarde la base, télécharge et vérifie le paquet, puis remplace les fichiers de l'application.</summary>
    public async Task<string> InstallAsync(CancellationToken ct = default)
    {
        if (!CanInstall) throw new ShopException("L'installation des mises à jour n'est possible que sous Linux.");
        if (Installed is { } done) throw new ShopException($"Version {done} déjà installée : redémarrez l'application.");
        if (Available is not { } update) throw new ShopException("Aucune mise à jour disponible.");
        if (update.PackageUrl is null) throw new ShopException($"La version {update.Version} n'a pas de paquet pour {Platform}.");
        if (update.Sha256 is null) throw new ShopException("Paquet sans empreinte SHA-256 : installation refusée.");
        if (!await busy.WaitAsync(0, ct)) throw new ShopException("Une mise à jour est déjà en cours.");
        var work = Path.Combine(dataDir, "update");
        try
        {
            var backup = Path.Combine(dataDir, "backups", $"avant-maj-{update.Version}-{DateTime.Now:yyyyMMdd-HHmmss}.db");
            Directory.CreateDirectory(Path.GetDirectoryName(backup)!);
            Backup.Save(Path.Combine(dataDir, "localmerch.db"), backup);

            if (Directory.Exists(work)) Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);
            var package = Path.Combine(work, "package.tar.gz");
            await using (var file = File.Create(package))
            await using (var download = await http.GetStreamAsync(update.PackageUrl, ct))
                await download.CopyToAsync(file, ct);

            await using (var file = File.OpenRead(package))
                if (!Convert.ToHexStringLower(await SHA256.HashDataAsync(file, ct)).Equals(update.Sha256, StringComparison.OrdinalIgnoreCase))
                    throw new ShopException("Paquet corrompu (empreinte SHA-256 différente) : installation annulée.");

            var extracted = Path.Combine(work, "files");
            Directory.CreateDirectory(extracted);
            await using (var file = File.OpenRead(package))
            await using (var gzip = new GZipStream(file, CompressionMode.Decompress))
                await TarFile.ExtractToDirectoryAsync(gzip, extracted, overwriteFiles: false, ct); // refuse les chemins hors du dossier
            var source = Path.Combine(extracted, "localmerch");
            if (!File.Exists(Path.Combine(source, "LocalMerch.App")))
                throw new ShopException("Paquet inattendu : LocalMerch.App introuvable.");

            // ponytail: remplacement fichier par fichier, une panne au milieu laisse une installation mélangée
            // (la base est sauvegardée) ; passer par un dossier versionné + lien symbolique si ça arrive.
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(installDir, relative);
                // Réglages locaux conservés.
                if (Path.GetFileName(relative).StartsWith("appsettings", StringComparison.OrdinalIgnoreCase) && File.Exists(target)) continue;
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Delete(target); // sous Linux, supprimer un exécutable en cours d'utilisation est permis, l'écraser non
                File.Move(file, target);
            }
            Installed = update.Version;
            Available = null;
            return backup;
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); } catch (IOException) { }
            busy.Release();
        }
    }
}
