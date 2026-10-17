using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    /// <summary>Answers HTTP requests in tests so nothing reaches the network.</summary>
    private sealed class FakeHttp(Func<HttpRequestMessage, HttpResponseMessage> answer) : HttpMessageHandler
    {
        public List<string> Urls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Urls.Add(request.RequestUri!.ToString());
            return Task.FromResult(answer(request));
        }
    }
    private static HttpResponseMessage Text(string body, HttpStatusCode code = HttpStatusCode.OK) => new(code) { Content = new StringContent(body, Encoding.UTF8) };
    private const string ReleasesJson = """
        [
          { "tag_name": "v0.3-dev-20", "name": "Draft", "html_url": "https://example.test/draft", "draft": true, "prerelease": true },
          { "tag_name": "v0.3-dev-20", "name": "Keyloom 0.3-dev-20", "html_url": "https://github.com/Shurielx/KeyLoom/releases/tag/v0.3-dev-20", "draft": false, "prerelease": true,
            "assets": [
              { "name": "SHA256SUMS.txt", "browser_download_url": "https://example.test/SHA256SUMS.txt", "size": 97 },
              { "name": "Keyloom-0.3-dev-20-win-x64.zip", "browser_download_url": "https://example.test/Keyloom-0.3-dev-20-win-x64.zip", "size": 1234, "digest": "sha256:ABCDEF" }
            ] },
          { "tag_name": "Alpha-01", "name": "Alpha", "html_url": "https://example.test/alpha", "draft": false, "prerelease": false },
          { "tag_name": "v0.1-dev-1", "name": "Keyloom 0.1-dev-1", "html_url": "https://example.test/1", "draft": false, "prerelease": true }
        ]
        """;
    private static string OnlineChangelog => "## 0.3-dev-20 · 2026-11-01\n\n### Featured\n- **Something new**, with a long line\n  that wraps onto the next one\n\n" + UpdateService.BundledChangelog();

    /// <summary>Reports progress right away, unlike Progress, which posts to a context.</summary>
    private sealed class SyncProgress(Action<double> report) : IProgress<double> { public void Report(double value) => report(value); }

    /// <summary>Runs async UI code on the dispatcher and pumps until it finishes.</summary>
    private static void RunOnDispatcher(Func<Task> work)
    {
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
        try
        {
            var task = work();
            var clock = Stopwatch.StartNew();
            while (!task.IsCompleted) { if (clock.ElapsedMilliseconds > 5000) throw new TimeoutException(); Pump(10); }
            task.GetAwaiter().GetResult();
        }
        finally { SynchronizationContext.SetSynchronizationContext(previous); }
    }

    private static void UpdateTests()
    {
        // Versions order development builds before the finished release; other tags are not versions.
        Check(UpdateService.IsNewer("0.1-dev-5", "0.1-dev-3") && UpdateService.IsNewer("0.1", "0.1-dev-9") && UpdateService.IsNewer("v0.1.1", "0.1")
            && !UpdateService.IsNewer("0.1-dev-3", "0.1-dev-3") && !UpdateService.IsNewer("0.1-dev-10", "0.3-dev-19") && UpdateService.ParseVersion("Alpha-01") == null,
            "versions compare development builds, releases and patches");

        // The bundled changelog parses and starts with this very version, so it cannot be forgotten.
        var bundled = UpdateService.ParseChangelog(UpdateService.BundledChangelog());
        // In-app notes stay short; long notes belong on the GitHub release.
        Check(bundled.SelectMany(e => e.Sections).SelectMany(x => x.Items).All(i => i.Length <= 60), "in-app changelog lines stay short");
        Check(bundled.Count >= 5 && bundled[0].Version == AppVersion.Display && bundled[0].Date.Length == 10
            && bundled[0].Sections.Count > 0 && bundled.Last().Version == "Alpha-01",
            "the bundled changelog covers every version and starts with the installed one");
        var wrapped = UpdateService.ParseChangelog(OnlineChangelog)[0].Sections[0].Items[0];
        Check(wrapped == "**Something new**, with a long line that wraps onto the next one", "changelog items join wrapped lines");

        // Releases: drafts and non-version tags are skipped, newest first.
        var online = new FakeHttp(r => r.RequestUri!.Host == "api.github.com" ? Text(ReleasesJson) : Text(OnlineChangelog));
        using var http = UpdateService.CreateClient(online);
        var releases = UpdateService.FetchReleasesAsync(http).GetAwaiter().GetResult()!;
        Check(releases.Select(r => r.Version).SequenceEqual(new[] { "0.3-dev-20", "0.1-dev-1" }) && UpdateService.NewerThan(releases, "0.3-dev-19")?.Version == "0.3-dev-20"
            && UpdateService.NewerThan(releases, "0.3-dev-20") == null && http.DefaultRequestHeaders.UserAgent.ToString().StartsWith("Keyloom/"),
            "update check finds the newest published release");
        using var down = UpdateService.CreateClient(new FakeHttp(_ => throw new HttpRequestException("offline")));
        Check(UpdateService.FetchReleasesAsync(down).GetAwaiter().GetResult() == null && UpdateService.FetchChangelogAsync(down).GetAwaiter().GetResult() == null,
            "being offline is reported as no answer, not an error");
        using var limited = UpdateService.CreateClient(new FakeHttp(_ => Text("{\"message\":\"rate limited\"}", HttpStatusCode.Forbidden)));
        Check(UpdateService.FetchReleasesAsync(limited).GetAwaiter().GetResult() == null, "GitHub errors are treated like being offline");
        Check(releases[0] is { DownloadUrl: "https://example.test/Keyloom-0.3-dev-20-win-x64.zip", Size: 1234, Sha256: "ABCDEF" } && releases[1].DownloadUrl == null,
            "a release brings its Windows ZIP and checksum");

        // Updating: the ZIP downloads, is checked against its checksum, and Keyloom.exe comes out of it.
        string temp = Path.Combine(Path.GetTempPath(), "keyloom-update-test-" + Guid.NewGuid().ToString("N"));
        try
        {
            var zipBytes = new MemoryStream();
            using (var zip = new ZipArchive(zipBytes, ZipArchiveMode.Create, true))
            using (var writer = new StreamWriter(zip.CreateEntry("Keyloom.exe").Open())) writer.Write("new build");
            string sha = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(zipBytes.ToArray()));
            using var files = UpdateService.CreateClient(new FakeHttp(_ => new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zipBytes.ToArray()) }));
            var release = releases[0] with { Sha256 = sha.ToLowerInvariant() };
            var seen = new List<double>();
            string exe = UpdateService.DownloadAsync(files, release, new SyncProgress(seen.Add), Path.Combine(temp, "work")).GetAwaiter().GetResult();
            Check(File.ReadAllText(exe) == "new build" && !File.Exists(Path.Combine(temp, "work", "Keyloom.zip")) && seen.Count > 0 && seen[^1] == 1,
                "an update downloads, passes its checksum and unpacks Keyloom.exe");
            bool damaged = false;
            try { UpdateService.DownloadAsync(files, release with { Sha256 = "00" }, null, Path.Combine(temp, "bad")).GetAwaiter().GetResult(); }
            catch (InvalidDataException) { damaged = true; }
            Check(damaged && !File.Exists(Path.Combine(temp, "bad", "Keyloom.exe")), "a download that does not match its checksum is refused");

            // Installing renames the Keyloom already there, so a running one can be replaced, and cleaning up removes it.
            string target = Path.Combine(temp, "Programs", "Keyloom", "Keyloom.exe");
            UpdateService.Install(exe, target);
            Check(File.ReadAllText(target) == "new build" && !File.Exists(exe), "the first install puts Keyloom in its own folder");
            File.WriteAllText(exe, "newer build");
            UpdateService.Install(exe, target);
            Check(File.ReadAllText(target) == "newer build" && File.ReadAllText(target + ".old") == "new build", "an update swaps the exe and keeps the old one aside");
            UpdateService.CleanUp(target, Path.Combine(temp, "work"));
            Check(!File.Exists(target + ".old") && !Directory.Exists(Path.Combine(temp, "work")) && File.Exists(target), "the next start removes what the update left");

            string link = Path.Combine(temp, "Start", "Keyloom.lnk");
            UpdateService.CreateShortcut(target, link);
            Check(File.Exists(link) && new FileInfo(link).Length > 0, "Keyloom gets a Start menu shortcut");
        }
        finally { try { Directory.Delete(temp, true); } catch (IOException) { } }

        // About page: online it shows the newer version and the GitHub changelog; offline it keeps the bundled one.
        AboutView? about = null;
        RunOnDispatcher(() => { about = new AboutView(http, "0.3-dev-19"); return about.CheckAsync(); });
        var entries = ((IEnumerable<ChangelogEntryView>)((ItemsControl)about!.FindName("ChangelogList")).ItemsSource).ToList();
        Check(((TextBlock)about.FindName("StatusText")).Text.Contains("0.3-dev-20 is out") && ((FrameworkElement)about.FindName("DownloadButton")).Visibility == Visibility.Visible
            && ((TextBlock)about.FindName("SourceText")).Text.Contains("GitHub"), "about shows a newer release with a download button");
        Check(entries[0] is { Version: "0.3-dev-20", IsNew: true } && entries[1] is { Version: "0.3-dev-19", IsInstalled: true, IsNew: false }, "about marks new and installed versions in the changelog");
        // The changelog shows one release line at a time, with only new and installed builds open.
        var lines = ((StackPanel)about.FindName("LinePanel")).Children.OfType<RadioButton>().ToList();
        Check(UpdateService.ReleaseLine("0.3-dev-19") == "0.3" && UpdateService.ReleaseLine("0.1") == "0.1" && UpdateService.ReleaseLine("Alpha-01") == "Alpha"
            && lines.Select(r => (string)r.Tag).SequenceEqual(["0.3", "0.2", "0.1", "Alpha"]) && lines[0].IsChecked == true
            && entries.All(e => e.Version.StartsWith("0.3-")) && entries[0].IsExpanded && entries[1].IsExpanded && !entries[2].IsExpanded
            && entries[2].Sections[0] is { Tone: "Added" }, "the changelog is grouped by release line, folded except for new and installed builds");
        lines[2].IsChecked = true;
        entries = ((IEnumerable<ChangelogEntryView>)((ItemsControl)about.FindName("ChangelogList")).ItemsSource).ToList();
        Check(about.Line == "0.1" && entries.All(e => e.Version.StartsWith("0.1-")) && entries[0].IsExpanded && entries.Skip(1).All(e => !e.IsExpanded),
            "another release line opens with its newest build");
        Invoke(about, "Fold_Click", about, new RoutedEventArgs());
        Check(entries.All(e => e.IsExpanded) && (string)((Button)about.FindName("FoldButton")).Content == "Fold all", "Open all opens every build in the line");
        RunOnDispatcher(() => { about = new AboutView(down, "0.3-dev-19"); return about.CheckAsync(); });
        entries = ((IEnumerable<ChangelogEntryView>)((ItemsControl)about!.FindName("ChangelogList")).ItemsSource).ToList();
        Check(((TextBlock)about.FindName("StatusText")).Text.Contains("Couldn't reach GitHub") && ((TextBlock)about.FindName("SourceText")).Text.Contains("Included")
            && entries[0].IsInstalled && ((Button)about.FindName("CheckButton")).IsEnabled, "offline, about keeps the bundled changelog and lets you try again");
        using var current = UpdateService.CreateClient(new FakeHttp(r => r.RequestUri!.Host == "api.github.com" ? Text(ReleasesJson) : Text(UpdateService.BundledChangelog())));
        RunOnDispatcher(() => { about = new AboutView(current, "0.3-dev-20"); return about.CheckAsync(); });
        Check(((TextBlock)about!.FindName("StatusText")).Text.Contains("latest version") && ((FrameworkElement)about.FindName("DownloadButton")).Visibility == Visibility.Collapsed,
            "about says when you have the latest version");

        // With an installer: the button updates in place, and a Keyloom that is not installed yet is offered the install.
        string? restartedWith = null;
        Task Restart(string exe) { restartedWith = exe; return Task.CompletedTask; }
        RunOnDispatcher(() => { about = new AboutView(http, "0.3-dev-19", Restart); return about.CheckAsync(); });
        Check(((TextBlock)about!.FindName("DownloadText")).Text == "Update to 0.3-dev-20", "about offers to update in place");
        RunOnDispatcher(() => { about = new AboutView(current, "0.3-dev-20", Restart); return about.CheckAsync(); });
        Check(((TextBlock)about!.FindName("DownloadText")).Text == "Install" && ((TextBlock)about.FindName("StatusText")).Text.Contains("Start menu")
            && ((FrameworkElement)about.FindName("DownloadButton")).Visibility == Visibility.Visible, "a Keyloom started from a ZIP is offered the install");
        using var failing = UpdateService.CreateClient(new FakeHttp(r => r.RequestUri!.Host == "api.github.com" ? Text(ReleasesJson)
            : r.RequestUri.AbsolutePath.EndsWith(".zip") ? Text("", HttpStatusCode.NotFound) : Text(OnlineChangelog)));
        RunOnDispatcher(async () => { about = new AboutView(failing, "0.3-dev-19", Restart); await about.CheckAsync(); await about.UpdateAsync(); });
        Check(restartedWith == null && ((TextBlock)about!.FindName("StatusText")).Text.Contains("didn't finish") && ((Button)about.FindName("DownloadButton")).IsEnabled,
            "a failed download says so, keeps this Keyloom running and lets you try again");
    }
}
