using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

namespace LithicBackup.Services;

/// <summary>
/// Details of a GitHub release that is newer than the running build.
/// </summary>
public sealed record UpdateInfo(
    Version Version,
    string TagName,
    string ReleaseName,
    string ReleaseNotes,
    string ReleasePageUrl,
    string? InstallerDownloadUrl,
    string? InstallerFileName);

/// <summary>
/// Outcome of an update check. Exactly one of <see cref="Update"/> /
/// <see cref="Error"/> is meaningful; when both are null the app is up to date.
/// </summary>
public sealed record UpdateCheckResult(
    UpdateInfo? Update,
    Version CurrentVersion,
    Version? LatestVersion,
    string? Error)
{
    public bool IsUpdateAvailable => Update is not null;
    public bool Failed => Error is not null;
}

/// <summary>
/// Checks GitHub Releases for a newer version of the app and locates the
/// installer asset to download. Prefers the bare <c>.msi</c> (the only artifact
/// current releases ship): the MSI closes a running — even elevated — GUI by
/// <em>asking</em> it to exit via the <c>SignalLithicGuiShutdown</c> custom
/// action before InstallValidate, and the in-app updater additionally shuts this
/// GUI down itself right after launching the installer, so no self-elevating
/// wrapper is needed. A legacy self-extracting <c>.exe</c> (the old WiX Burn
/// bundle used up to v1.0.10, since retired — <c>installer\Bundle.wxs</c> no
/// longer exists) is still accepted only as a fallback if a release happens to
/// attach one. Pure network/parse logic with no UI dependencies so it can be
/// unit-tested and reused from the GUI or Worker.
/// </summary>
public static class UpdateService
{
    // The public repo that publishes tagged releases (v1.2.3) with the MSI attached.
    private const string Owner = "inhahe";
    private const string Repo = "Lithic";

    private static readonly string LatestReleaseUrl =
        $"https://api.github.com/repos/{Owner}/{Repo}/releases/latest";

    // A single shared client: GitHub requires a User-Agent, and reusing one
    // HttpClient avoids socket exhaustion.
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.Add(
            new ProductInfoHeaderValue("LithicBackup", "1.0"));
        http.DefaultRequestHeaders.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return http;
    }

    /// <summary>
    /// Queries the latest GitHub release and compares its version to
    /// <paramref name="currentVersion"/>. Never throws: network/parse failures
    /// come back as <see cref="UpdateCheckResult.Error"/> so callers can decide
    /// whether to surface them (a background startup check stays silent; a
    /// user-initiated check reports the failure).
    /// </summary>
    public static async Task<UpdateCheckResult> CheckForUpdateAsync(
        Version currentVersion, CancellationToken ct = default)
    {
        try
        {
            using var resp = await Http.GetAsync(LatestReleaseUrl, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                var reason = (int)resp.StatusCode == 404
                    ? "No published releases found."
                    : $"GitHub returned {(int)resp.StatusCode} {resp.ReasonPhrase}.";
                return new UpdateCheckResult(null, currentVersion, null, reason);
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            var root = doc.RootElement;

            var tag = root.TryGetProperty("tag_name", out var t) ? t.GetString() ?? "" : "";
            var latest = ParseVersion(tag);
            if (latest is null)
                return new UpdateCheckResult(null, currentVersion, null,
                    $"Could not parse a version from release tag '{tag}'.");

            if (Normalize(latest) <= Normalize(currentVersion))
                return new UpdateCheckResult(null, currentVersion, latest, null); // up to date

            var name = root.TryGetProperty("name", out var n) ? n.GetString() ?? tag : tag;
            var notes = root.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "";
            var page = root.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "";

            // Prefer the bare .msi (what current releases ship); accept a legacy
            // .exe bundle only as a fallback if that's the only asset attached. The
            // MSI's SignalLithicGuiShutdown custom action (plus the updater closing
            // this GUI itself) means the old self-elevating .exe is no longer
            // required to beat the file-in-use check (see class summary).
            string? exeUrl = null, exeName = null;
            string? msiUrl = null, msiName = null;
            if (root.TryGetProperty("assets", out var assets) &&
                assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var asset in assets.EnumerateArray())
                {
                    var an = asset.TryGetProperty("name", out var ap) ? ap.GetString() : null;
                    if (an is null) continue;
                    var url = asset.TryGetProperty("browser_download_url", out var du)
                        ? du.GetString() : null;

                    if (exeUrl is null && an.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        exeName = an;
                        exeUrl = url;
                    }
                    else if (msiUrl is null && an.EndsWith(".msi", StringComparison.OrdinalIgnoreCase))
                    {
                        msiName = an;
                        msiUrl = url;
                    }
                }
            }

            var installerUrl = msiUrl ?? exeUrl;
            var installerName = msiUrl is not null ? msiName : exeName;

            var info = new UpdateInfo(latest, tag, name, notes, page, installerUrl, installerName);
            return new UpdateCheckResult(info, currentVersion, latest, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult(null, currentVersion, null, ex.Message);
        }
    }

    /// <summary>
    /// Downloads the release installer (the self-elevating <c>.exe</c> bundle when
    /// present, otherwise the <c>.msi</c>) to a temp file and returns its path.
    /// Throws on failure so the caller can report it.
    /// </summary>
    public static async Task<string> DownloadInstallerAsync(
        UpdateInfo info, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(info.InstallerDownloadUrl))
            throw new InvalidOperationException("This release has no installer asset to download.");

        var fileName = info.InstallerFileName ?? $"LithicBackup-{info.Version}-x64.msi";
        var dest = Path.Combine(Path.GetTempPath(), fileName);

        using var resp = await Http.GetAsync(
            info.InstallerDownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var total = resp.Content.Headers.ContentLength;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = File.Create(dest);

        var buffer = new byte[81920];
        long read = 0;
        int n;
        while ((n = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
            read += n;
            if (total is > 0)
                progress?.Report((double)read / total.Value);
        }

        return dest;
    }

    /// <summary>Parses a GitHub tag like "v1.2.3" or "1.2.3" into a Version.</summary>
    private static Version? ParseVersion(string? tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        var s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase))
            s = s[1..];
        return Version.TryParse(s, out var v) ? v : null;
    }

    /// <summary>
    /// Coerces a Version to a full 4-part form (missing parts -> 0) so
    /// comparisons are consistent. Version leaves absent Build/Revision as -1,
    /// which would make "1.0.3" compare as OLDER than the assembly's "1.0.3.0".
    /// </summary>
    private static Version Normalize(Version v) =>
        new(v.Major, v.Minor, Math.Max(v.Build, 0), Math.Max(v.Revision, 0));
}
