using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Keyloom.Services;

public sealed record ChangelogSection(string Title, IReadOnlyList<string> Items);
public sealed record ChangelogEntry(string Version, string Date, IReadOnlyList<ChangelogSection> Sections);
/// <summary>A published release on GitHub, with its Windows ZIP when it has one.</summary>
public sealed record ReleaseInfo(string Version, string Name, string Url, bool IsPrerelease, string? DownloadUrl = null, long Size = 0, string? Sha256 = null);
public enum ChangelogSource { Bundled, GitHub }

/// <summary>
/// The changelog, update check and updater. The changelog ships inside the app (CHANGELOG.md) and is refreshed from GitHub when online;
/// the update check compares this version with the releases published on GitHub. An update downloads the release ZIP, checks it,
/// and swaps Keyloom.exe in its own folder, where the Start menu finds it: Windows lets a running exe be renamed, not overwritten.
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
    /// <summary>The release a build belongs to: 0.3-dev-16 and 0.3.1 are both 0.3. Labels that are not versions keep their name, so Alpha-01 is Alpha.</summary>
    public static string ReleaseLine(string version) =>
        ParseVersion(version) is { } v ? $"{v.Major}.{v.Minor}" : version.Split('-')[0].Trim();

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
                var zip = r.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array
                    ? assets.EnumerateArray().FirstOrDefault(a => (a.GetProperty("name").GetString() ?? "").EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase))
                    : default;
                bool hasZip = zip.ValueKind == JsonValueKind.Object;
                string? digest = hasZip && zip.TryGetProperty("digest", out var d) && d.GetString() is { } sha && sha.StartsWith("sha256:") ? sha[7..] : null;
                releases.Add(new(version, r.TryGetProperty("name", out var name) ? name.GetString() ?? tag : tag,
                    r.GetProperty("html_url").GetString() ?? ReleasesUrl, r.TryGetProperty("prerelease", out var pre) && pre.GetBoolean(),
                    hasZip ? zip.GetProperty("browser_download_url").GetString() : null,
                    hasZip && zip.TryGetProperty("size", out var size) ? size.GetInt64() : 0, digest));
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

    // ---- Updating ----

    /// <summary>Passed to the new Keyloom after an update, so it waits for the old one to close instead of saying it is already running.</summary>
    public const string UpdatedArgument = "--updated";
    /// <summary>Where Keyloom lives once installed: a folder of its own, no administrator needed. Macros and settings stay in AppData.</summary>
    public static string InstallFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Keyloom");
    public static string InstalledExe => Path.Combine(InstallFolder, "Keyloom.exe");
    private static string WorkFolder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Keyloom", "Update");
    /// <summary>The Start menu entry, so typing "Keyloom" in Windows finds it.</summary>
    public static string StartMenuShortcut => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Keyloom.lnk");
    public static bool IsInstalled => string.Equals(Environment.ProcessPath, InstalledExe, StringComparison.OrdinalIgnoreCase);

    /// <summary>Downloads the release ZIP, checks it against GitHub's SHA-256, and unpacks Keyloom.exe. Returns the unpacked exe.</summary>
    public static async Task<string> DownloadAsync(HttpClient http, ReleaseInfo release, IProgress<double>? progress = null, string? folder = null, CancellationToken token = default)
    {
        if (release.DownloadUrl == null) throw new InvalidOperationException(L.T($"Keyloom {release.Version} has no Windows download."));
        folder ??= WorkFolder;
        if (Directory.Exists(folder)) Directory.Delete(folder, true);
        Directory.CreateDirectory(folder);
        string zipPath = Path.Combine(folder, "Keyloom.zip"), exePath = Path.Combine(folder, "Keyloom.exe");
        using (var response = await http.GetAsync(release.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false))
        {
            response.EnsureSuccessStatusCode();
            long total = response.Content.Headers.ContentLength ?? release.Size;
            var source = await response.Content.ReadAsStreamAsync(token).ConfigureAwait(false);
            await using var _ = source.ConfigureAwait(false);
            var file = File.Create(zipPath);
            await using var __ = file.ConfigureAwait(false);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[81920];
            long done = 0;
            for (int read; (read = await source.ReadAsync(buffer, token).ConfigureAwait(false)) > 0;)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                hash.AppendData(buffer, 0, read);
                done += read;
                if (total > 0) progress?.Report(Math.Min(1, (double)done / total));
            }
            if (release.Sha256 != null && !Convert.ToHexString(hash.GetHashAndReset()).Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException(L.T("The download was damaged on the way. Try again."));
        }
        using (var zip = ZipFile.OpenRead(zipPath))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name.Equals("Keyloom.exe", StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidDataException(L.T("The download has no Keyloom.exe in it."));
            entry.ExtractToFile(exePath, true);
        }
        File.Delete(zipPath);
        return exePath;
    }
    /// <summary>
    /// Puts <paramref name="newExe"/> at <paramref name="target"/>. A Keyloom already there, even a running one, is renamed
    /// to Keyloom.exe.old first, and put back if the new one cannot be moved in.
    /// </summary>
    public static void Install(string newExe, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        string old = target + ".old";
        if (File.Exists(old)) File.Delete(old);
        bool moved = false;
        if (File.Exists(target)) { File.Move(target, old); moved = true; }
        try { File.Move(newExe, target); }
        catch when (moved) { File.Move(old, target); throw; }
    }
    /// <summary>Removes what an update leaves behind: the old exe and the download folder. Whatever is still locked stays for next time.</summary>
    public static void CleanUp(string? exe = null, string? folder = null)
    {
        folder ??= WorkFolder;
        try { File.Delete((exe ?? Environment.ProcessPath ?? "") + ".old"); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        try { if (Directory.Exists(folder)) Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    /// <summary>Makes a shortcut to <paramref name="exe"/>; by default the Start menu entry.</summary>
    public static void CreateShortcut(string exe, string? path = null)
    {
        path ??= StartMenuShortcut;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var shellType = Type.GetTypeFromProgID("WScript.Shell") ?? throw new InvalidOperationException(L.T("Windows can't make shortcuts on this PC."));
        dynamic shell = Activator.CreateInstance(shellType)!;
        try
        {
            dynamic link = shell.CreateShortcut(path);
            link.TargetPath = exe;
            link.WorkingDirectory = Path.GetDirectoryName(exe);
            link.IconLocation = exe + ",0";
            link.Description = "Keyboard and mouse macros for Windows";
            link.Save();
            System.Runtime.InteropServices.Marshal.FinalReleaseComObject(link);
        }
        finally { System.Runtime.InteropServices.Marshal.FinalReleaseComObject(shell); }
    }
    /// <summary>Starts the Keyloom at <paramref name="exe"/> as the one that takes over after an update.</summary>
    public static void Launch(string exe) =>
        Process.Start(new ProcessStartInfo(exe, UpdatedArgument) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(exe) });

    private static readonly Lazy<HttpClient> SharedClient = new(() => CreateClient());
    /// <summary>One client for the About page and the check at start.</summary>
    public static HttpClient Shared => SharedClient.Value;
    /// <summary>A client that identifies Keyloom to GitHub, which requires a user agent.</summary>
    public static HttpClient CreateClient(HttpMessageHandler? handler = null)
    {
        var http = handler == null ? new HttpClient() : new HttpClient(handler);
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Keyloom", AppVersion.Display.Replace(' ', '-')));
        return http;
    }
}
