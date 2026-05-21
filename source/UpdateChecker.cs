using System.Net.Http;
using System.Text.Json;

namespace SurfaceChargingTray;

/// <summary>
/// Lightweight GitHub-release update checker. Hits the public Releases API
/// once per 24h max (throttled via LastUpdateCheckAt in settings.ini) and
/// returns a Result indicating whether a newer version is available.
///
/// Why throttled rather than per-launch: many users launch the tray app
/// multiple times per day. One API call per launch would burn through
/// GitHub's anonymous rate limit (60/hr per IP) on a heavy day and could
/// also flag a misbehaving app. Once-per-day is plenty for "is there a
/// new release available" — users who want immediate awareness can also
/// watch the GitHub repo.
///
/// Network is best-effort: any error (no internet, rate limited, API
/// schema changed, etc.) is swallowed silently. Update checking is a
/// nice-to-have, not a must-have, so we never bubble errors to the user.
/// </summary>
internal static class UpdateChecker
{
    private const string ReleasesApiUrl =
        "https://api.github.com/repos/keyokku/SurfaceChargingTray/releases/latest";
    private const string ReleasesWebUrl =
        "https://github.com/keyokku/SurfaceChargingTray/releases/latest";

    // Shared HttpClient — recommended practice (per HttpClient docs, don't
    // dispose+recreate per call; reuse for the process lifetime).
    private static readonly HttpClient _http = CreateClient();

    private static HttpClient CreateClient()
    {
        var c = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(10)
        };
        // GitHub API requires a User-Agent header. Send our app name + version.
        c.DefaultRequestHeaders.UserAgent.ParseAdd(
            $"SurfaceChargingTray/{typeof(UpdateChecker).Assembly.GetName().Version}");
        c.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
        return c;
    }

    public sealed class Result
    {
        /// <summary>The version tag returned by GitHub (e.g. "v1.4.0"). Null on failure.</summary>
        public string? LatestTag { get; set; }
        /// <summary>Whether a newer tag than the running app version was found.</summary>
        public bool    UpdateAvailable { get; set; }
        /// <summary>URL to send the user to if they click the balloon.</summary>
        public string  ReleasesUrl => ReleasesWebUrl;
    }

    /// <summary>
    /// Returns the cached latest version + update-available flag if the
    /// last check was within 24h; otherwise hits the API, caches the
    /// result, and returns it. Always returns quickly (no blocking I/O
    /// if cached). Call from a background Task — do not invoke on the UI thread.
    /// </summary>
    public static async Task<Result?> CheckAsync(SettingsModel settings, string currentAppVersion)
    {
        // 24h throttle check
        if (!string.IsNullOrEmpty(settings.LastUpdateCheckAt) &&
            DateTime.TryParse(settings.LastUpdateCheckAt, out var lastAt) &&
            (DateTime.UtcNow - lastAt.ToUniversalTime()).TotalHours < 24)
        {
            // Within throttle window — return cached result
            if (!string.IsNullOrEmpty(settings.LatestKnownVersion))
            {
                return new Result
                {
                    LatestTag       = settings.LatestKnownVersion,
                    UpdateAvailable = IsNewer(settings.LatestKnownVersion!, currentAppVersion)
                };
            }
            return null;   // throttled and no cache — skip this round
        }

        try
        {
            using var resp = await _http.GetAsync(ReleasesApiUrl).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return null;
            var json = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);

            // Standard GitHub Releases schema: top-level "tag_name" field.
            if (!doc.RootElement.TryGetProperty("tag_name", out var tagEl)) return null;
            var tag = tagEl.GetString();
            if (string.IsNullOrEmpty(tag)) return null;

            // Update cache regardless of update-available (so we throttle).
            settings.LastUpdateCheckAt  = DateTime.UtcNow.ToString("o");
            settings.LatestKnownVersion = tag;
            settings.Save();

            return new Result
            {
                LatestTag       = tag,
                UpdateAvailable = IsNewer(tag, currentAppVersion)
            };
        }
        catch (Exception ex)
        {
            // Log but never throw. Update checking is opportunistic.
            try { Logger.Error($"[INFO] UpdateChecker: {ex.GetType().Name}: {ex.Message}"); } catch { }
            return null;
        }
    }

    /// <summary>
    /// Strict-but-tolerant version comparison. Accepts tags like "v1.4.0",
    /// "1.4.0", "v1.4.0-beta1". Strips a leading 'v' and any pre-release
    /// suffix, then compares parsed System.Version.
    /// </summary>
    public static bool IsNewer(string remoteTag, string localVersion)
    {
        try
        {
            var remote = NormalizeVersion(remoteTag);
            var local  = NormalizeVersion(localVersion);
            return remote.CompareTo(local) > 0;
        }
        catch { return false; }
    }

    private static Version NormalizeVersion(string tag)
    {
        var s = tag.Trim();
        if (s.StartsWith("v", StringComparison.OrdinalIgnoreCase)) s = s[1..];
        // Strip pre-release / build suffix
        int dash = s.IndexOf('-');
        if (dash >= 0) s = s[..dash];
        int plus = s.IndexOf('+');
        if (plus >= 0) s = s[..plus];
        return Version.Parse(s);
    }
}
