using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;

namespace Dovetail.Core;

/// <summary>
/// Updates Dovetail itself from its own GitHub releases.
///
/// No update-metadata file is needed: the releases API already carries the tag, the installer
/// asset and GitHub's own SHA-256 of that asset. A release without that digest is refused
/// rather than installed unchecked, because this build is not code-signed and the digest is
/// the only thing tying the downloaded file to the release.
///
/// Every failure surfaces as an <see cref="UpdateException"/> or the underlying network error,
/// and the caller's fallback is <see cref="DownloadPage"/>, never the GitHub releases page.
/// </summary>
public static class AppUpdate
{
    public const string DownloadPage = "https://isaac-onyango-dev.github.io/Dovetail/";

    private const string LatestApi = "https://api.github.com/repos/Isaac-Onyango-Dev/Dovetail/releases/latest";
    private const string UserAgent = "Dovetail-Updater";

    /// <summary>Tells the installer it was started by the updater: it relaunches Dovetail on
    /// success and opens <see cref="DownloadPage"/> on failure. See packaging/Dovetail.iss.</summary>
    private const string UpdateSwitch = "/DOVETAILUPDATE";

    public sealed record Release(Version Version, string Tag, string AssetName, string Url, string Sha256, long Size);

    public sealed class UpdateException(string message) : Exception(message);

    /// <summary>Major.minor.build, so "1.1.0" and assembly version "1.1.0.0" compare equal.</summary>
    public static Version Normalize(Version v) => new(v.Major, v.Minor, Math.Max(v.Build, 0));

    /// <summary>
    /// True when this copy was put here by the installer, which leaves its uninstaller beside
    /// the executable. A portable copy, or a development build, has none and cannot be updated
    /// by running the installer: that would install a second copy rather than replace this one.
    /// </summary>
    public static bool IsInstalledCopy =>
        Directory.EnumerateFiles(AppContext.BaseDirectory, "unins*.exe").Any();

    public static async Task<Release> GetLatestAsync(CancellationToken ct = default)
    {
        using var http = Client(TimeSpan.FromSeconds(30));
        http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        using var resp = await http.GetAsync(LatestApi, ct);
        if (!resp.IsSuccessStatusCode)
            throw new UpdateException($"GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}.");
        return Parse(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Picks the installer out of a releases-API response. Public for the self test.</summary>
    public static Release Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        string tag = Str(root, "tag_name");
        if (!Version.TryParse(tag.TrimStart('v', 'V'), out var version))
            throw new UpdateException($"The latest release tag \"{tag}\" is not a version number.");

        if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in assets.EnumerateArray())
            {
                string name = Str(a, "name");
                if (!name.StartsWith("DovetailSetup-", StringComparison.OrdinalIgnoreCase)
                    || !name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    || Path.GetFileName(name) != name)
                    continue;

                string url = Str(a, "browser_download_url");
                if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                    throw new UpdateException($"{name} has no download address.");

                string digest = Str(a, "digest");
                if (!digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) || digest.Length != 7 + 64)
                    throw new UpdateException($"GitHub has no SHA-256 checksum for {name}, so it can't be checked.");

                long size = a.TryGetProperty("size", out var s) && s.TryGetInt64(out var n) ? n : 0;
                return new Release(Normalize(version), tag, name, url, digest[7..], size);
            }
        }
        throw new UpdateException($"Release {tag} has no installer attached.");
    }

    /// <summary>
    /// Downloads the installer to %TEMP% and checks it against GitHub's digest. Progress is
    /// reported as 0..1, once per whole percent.
    /// </summary>
    public static async Task<string> DownloadAsync(Release release, IProgress<double>? progress,
                                                   CancellationToken ct = default)
    {
        string dir = Path.Combine(Path.GetTempPath(), "Dovetail", "update");
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, release.AssetName);

        using (var http = Client(TimeSpan.FromMinutes(15)))
        using (var resp = await http.GetAsync(release.Url, HttpCompletionOption.ResponseHeadersRead, ct))
        {
            if (!resp.IsSuccessStatusCode)
                throw new UpdateException($"The download failed: GitHub answered {(int)resp.StatusCode} {resp.ReasonPhrase}.");
            long total = resp.Content.Headers.ContentLength ?? release.Size;

            await using var src = await resp.Content.ReadAsStreamAsync(ct);
            await using var dst = File.Create(file);
            var buffer = new byte[81920];
            long done = 0;
            int read, lastPercent = -1;
            while ((read = await src.ReadAsync(buffer, ct)) > 0)
            {
                await dst.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                int percent = total > 0 ? (int)(done * 100 / total) : 0;
                if (percent != lastPercent) { lastPercent = percent; progress?.Report(percent / 100.0); }
            }
        }

        string actual;
        await using (var s = File.OpenRead(file))
            actual = Convert.ToHexString(await SHA256.HashDataAsync(s, ct));
        if (!string.Equals(actual, release.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(file);
            throw new UpdateException("The downloaded installer doesn't match GitHub's checksum, so it was deleted.");
        }
        return file;
    }

    /// <summary>
    /// Starts the installer elevated, with its own progress window visible and no way to cancel
    /// half way. Returns once Windows has accepted the elevation prompt; the caller should then
    /// exit so the installer can replace the files. Throws Win32Exception 1223 if the person
    /// declines the prompt.
    /// </summary>
    public static void StartInstaller(string file)
    {
        var psi = new ProcessStartInfo(file) { UseShellExecute = true, Verb = "runas" };
        foreach (var arg in new[] { "/SILENT", "/NOCANCEL", "/NORESTART", "/CLOSEAPPLICATIONS", UpdateSwitch })
            psi.ArgumentList.Add(arg);
        _ = Process.Start(psi) ?? throw new UpdateException("Windows didn't start the installer.");
    }

    /// <summary>Opens the download page in the default browser, outside Dovetail.</summary>
    public static void OpenDownloadPage() =>
        Process.Start(new ProcessStartInfo(DownloadPage) { UseShellExecute = true })?.Dispose();

    private static HttpClient Client(TimeSpan timeout)
    {
        var http = new HttpClient { Timeout = timeout };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        return http;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
}
