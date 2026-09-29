using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PeakRelay.Launcher.Core;

/// <summary>One downloadable file attached to a release.</summary>
public sealed record ReleaseAsset(string Name, long Size, long Id, string Url);

/// <summary>One GitHub release, reduced to what the launcher needs.</summary>
public sealed record ReleaseInfo(string Tag, string Name, bool Prerelease, IReadOnlyList<ReleaseAsset> Assets);

/// <summary>
/// Talks to api.github.com for the PingHugger/PeakRelay releases — the launcher's update
/// channel. On a public repo everything works anonymously. On a private repo the release
/// API 404s without credentials; set PEAKRELAY_GH_TOKEN (classic PAT with `repo` read or
/// fine-grained read-only contents) and the launcher authenticates. The token is read
/// from the environment at runtime only — it is never embedded in or persisted by the
/// shipped binary.
/// </summary>
public sealed class ReleaseClient : IDisposable
{
    public const string Repo = "PingHugger/PeakRelay";
    public const string TokenEnvVar = "PEAKRELAY_GH_TOKEN";

    private readonly HttpClient _http;

    public ReleaseClient(HttpClient? http = null)
    {
        _http = http ?? new HttpClient();
        _http.BaseAddress ??= new Uri($"https://api.github.com/repos/{Repo}/");
        if (_http.DefaultRequestHeaders.UserAgent.Count == 0)
            _http.DefaultRequestHeaders.UserAgent.ParseAdd("PeakRelay-Launcher");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        var token = TokenFromEnvironment();
        if (token != null)
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    }

    /// <summary>The env token, or null. Whitespace-only counts as unset.</summary>
    public static string? TokenFromEnvironment()
    {
        var value = Environment.GetEnvironmentVariable(TokenEnvVar);
        return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }

    /// <summary>The newest non-draft release (prereleases included, flagged).</summary>
    public async Task<ReleaseInfo?> LatestAsync(CancellationToken token = default)
    {
        using var response = await _http.GetAsync("releases/latest", token).ConfigureAwait(false);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null; // repo with no releases yet (or private without a token)
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        return Decode(doc.RootElement);
    }

    /// <summary>All non-draft releases, newest first.</summary>
    public async Task<IReadOnlyList<ReleaseInfo>> ListAsync(CancellationToken token = default)
    {
        using var response = await _http.GetAsync("releases?per_page=20", token).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: token).ConfigureAwait(false);
        return doc.RootElement.EnumerateArray().Select(Decode).ToList();
    }

    internal static ReleaseInfo Decode(JsonElement element)
    {
        var assets = element.GetProperty("assets").EnumerateArray()
            .Select(a => new ReleaseAsset(
                a.GetProperty("name").GetString() ?? "",
                a.GetProperty("size").GetInt64(),
                a.GetProperty("id").GetInt64(),
                a.GetProperty("browser_download_url").GetString() ?? ""))
            .ToList();
        return new ReleaseInfo(
            element.GetProperty("tag_name").GetString() ?? "",
            element.GetProperty("name").GetString() ?? "",
            element.GetProperty("prerelease").GetBoolean(),
            assets);
    }

    /// <summary>
    /// Update decision against the running launcher. "none" when the latest release is not
    /// newer, or is the one the user already declined (SkippedTag).
    /// </summary>
    public static (string Decision, ReleaseInfo? Release) CheckForUpdate(
        IEnumerable<ReleaseInfo>? releases, string? skippedTag)
    {
        var latest = releases?.FirstOrDefault();
        if (latest == null)
            return ("none", null);
        if (VersionDiffers(latest.Tag, RunningVersion()) && latest.Tag != skippedTag)
            return ("update", latest);
        return ("none", null);
    }

    /// <summary>AssemblyInformationalVersion of the running launcher (Directory.Build.props).</summary>
    public static string RunningVersion() =>
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
            ?.InformationalVersion ?? "0.0.0";

    /// <summary>
    /// True when the release tag denotes a different version than the running one.
    /// Tag forms: "v0.6.0", "v0.6.0-beta.2", or a bare "0.6.0" (release.yml would have to
    /// produce that) — the leading "v" is optional junk either way.
    /// </summary>
    public static bool VersionDiffers(string tag, string running)
    {
        if (!TryParseVersion(tag, out var tagVersion))
            return true; // unparsable tag → never pretend we are up to date
        var runningVersion = running.TrimStart('v').Split('+')[0]; // drop build metadata
        return tagVersion != runningVersion;
    }

    private static bool TryParseVersion(string tag, out string version)
    {
        version = tag.TrimStart('v');
        return version.Split('-')[0].Split('.').Length is 2 or 3
               && version.Split('-')[0].Split('.').All(part => int.TryParse(part, out _));
    }

    /// <summary>
    /// Streams an asset to destinationPath. Private repos cannot fetch the
    /// browser_download_url (it needs a web session), so assets are fetched through the
    /// API endpoint with Accept: application/octet-stream — GitHub then serves the raw
    /// binary under the same authorization. Public repos behave identically through this
    /// endpoint, so one code path serves both.
    /// </summary>
    public async Task<long> DownloadAsync(ReleaseAsset asset, string destinationPath,
        CancellationToken token = default, Action<long>? onProgress = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"releases/assets/{asset.Id}");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
        await using var target = File.Create(destinationPath);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0)
        {
            await target.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
            total += read;
            onProgress?.Invoke(total);
        }
        return total;
    }

    public void Dispose() => _http.Dispose();
}
