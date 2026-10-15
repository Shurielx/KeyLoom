using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Keyloom.Services;

public sealed record ChangelogSection(string Title, IReadOnlyList<string> Items);
public sealed record ChangelogEntry(string Version, string Date, IReadOnlyList<ChangelogSection> Sections);
/// <summary>A published release on GitHub.</summary>
public sealed record ReleaseInfo(string Version, string Name, string Url, bool IsPrerelease);
public enum ChangelogSource { Bundled, GitHub }

/// <summary>
/// The changelog and update check. The changelog ships inside the app (CHANGELOG.md) and is refreshed from GitHub when online;
/// the update check compares this version with the releases published on GitHub. Nothing runs until the About page asks.
/// </summary>
public static partial class UpdateService
{
    public const string Repository = "Shurielx/KeyLoom";
    public const string ReleasesUrl = $"https://github.com/{Repository}/releases";
    private const string ChangelogUrl = $"https://raw.githubusercontent.com/{Repository}/main/CHANGELOG.md";
    private const string ReleasesApi = $"https://api.github.com/repos/{Repository}/releases?per_page=30";
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(8);

    // ---- Versions ----

    [GeneratedRegex(@"^v?(\d+)\.(\d+)(?:\.(\d+))?(?:-dev-(\d+))?$", RegexOptions.IgnoreCase)]
    private static partial Regex VersionPattern();
    /// <summary>Orders versions like 0.1-dev-3 &lt; 0.1-dev-4 &lt; 0.1 &lt; 0.1.1. Returns null for labels that are not versions, such as Alpha-01.</summary>
    public static (int Major, int Minor, int Patch, int Dev)? ParseVersion(string text)
    {
        var m = VersionPattern().Match(text.Trim());
        if (!m.Success) return null;
        int Part(int group, int fallback) => m.Groups[group].Success ? int.Parse(m.Groups[group].Value) : fallback;
        // A finished release sorts after all of its development builds.
        return (Part(1, 0), Part(2, 0), Part(3, 0), Part(4, int.MaxValue));
    }
    public static bool IsNewer(string candidate, string current) =>
        ParseVersion(candidate) is { } a && ParseVersion(current) is { } b && a.CompareTo(b) > 0;

    // ---- Changelog ----

    /// <summary>Reads "## version · date" entries with "### Section" headings and "- " items. Items wrap onto indented lines.</summary>
    public static IReadOnlyList<ChangelogEntry> ParseChangelog(string markdown)
    {
        var entries = new List<ChangelogEntry>();
        string? version = null, date = null, section = null;
        var sections = new List<ChangelogSection>();
        var items = new List<string>();
        void CloseSection()
        {
            if (section != null && items.Count > 0) sections.Add(new(section, items.ToList()));
            items.Clear();
        }
        void CloseEntry()
        {
            CloseSection();
            if (version != null) entries.Add(new(version, date ?? "", sections.ToList()));
            sections.Clear(); section = null;
        }
        foreach (var raw in markdown.Replace("\r", "").Split('\n'))
        {
            string line = raw.TrimEnd();
            if (line.StartsWith("## "))
            {
                CloseEntry();
                var parts = line[3..].Split('·', 2, StringSplitOptions.TrimEntries);
                version = parts[0]; date = parts.Length > 1 ? parts[1] : "";
            }
            else if (line.StartsWith("### ") && version != null) { CloseSection(); section = line[4..].Trim(); }
            else if (line.TrimStart().StartsWith("- ") && version != null) { section ??= "Changes"; items.Add(line.TrimStart()[2..].Trim()); }
            else if (line.StartsWith("  ") && items.Count > 0 && line.Trim().Length > 0) items[^1] += " " + line.Trim();
        }
        CloseEntry();
        return entries;
    }
    /// <summary>The changelog that shipped with this build.</summary>
    public static string BundledChangelog()
    {
        using var stream = typeof(UpdateService).Assembly.GetManifestResourceStream("Keyloom.CHANGELOG.md");
        if (stream == null) return "";
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    /// <summary>The newest changelog from GitHub, or null when it cannot be reached or does not parse.</summary>
    public static async Task<string?> FetchChangelogAsync(HttpClient http, CancellationToken token = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(Timeout);
            string text = await http.GetStringAsync(ChangelogUrl, timeout.Token);
            return ParseChangelog(text).Count > 0 ? text : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException) { return null; }
    }

    // ---- Releases ----

    /// <summary>Published releases, newest version first. Null when GitHub cannot be reached.</summary>
    public static async Task<IReadOnlyList<ReleaseInfo>?> FetchReleasesAsync(HttpClient http, CancellationToken token = default)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            timeout.CancelAfter(Timeout);
            using var request = new HttpRequestMessage(HttpMethod.Get, ReleasesApi);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using var response = await http.SendAsync(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
            var releases = new List<ReleaseInfo>();
            foreach (var r in json.RootElement.EnumerateArray())
            {
                if (r.TryGetProperty("draft", out var draft) && draft.GetBoolean()) continue;
                string tag = r.GetProperty("tag_name").GetString() ?? "";
                string version = tag.TrimStart('v', 'V');
                if (ParseVersion(version) == null) continue;
                releases.Add(new(version, r.TryGetProperty("name", out var name) ? name.GetString() ?? tag : tag,
                    r.GetProperty("html_url").GetString() ?? ReleasesUrl, r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()));
            }
            return releases.OrderByDescending(r => ParseVersion(r.Version)).ToList();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or OperationCanceledException or JsonException or InvalidOperationException or KeyNotFoundException)
        {
            return null;
        }
    }
    /// <summary>The newest release that is newer than <paramref name="current"/>, if any.</summary>
    public static ReleaseInfo? NewerThan(IEnumerable<ReleaseInfo> releases, string current) =>
        releases.Where(r => IsNewer(r.Version, current)).MaxBy(r => ParseVersion(r.Version));

    /// <summary>A client that identifies Keyloom to GitHub, which requires a user agent.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Keyloom", AppVersion.Display.Replace(' ', '-')));
        return http;
    }
}
