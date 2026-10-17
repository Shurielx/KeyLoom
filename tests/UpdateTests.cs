using System.Diagnostics;
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
          { "tag_name": "v0.3-dev-13", "name": "Draft", "html_url": "https://example.test/draft", "draft": true, "prerelease": true },
          { "tag_name": "v0.3-dev-13", "name": "Keyloom 0.3-dev-13", "html_url": "https://github.com/Shurielx/KeyLoom/releases/tag/v0.3-dev-13", "draft": false, "prerelease": true },
          { "tag_name": "Alpha-01", "name": "Alpha", "html_url": "https://example.test/alpha", "draft": false, "prerelease": false },
          { "tag_name": "v0.1-dev-1", "name": "Keyloom 0.1-dev-1", "html_url": "https://example.test/1", "draft": false, "prerelease": true }
        ]
        """;
    private static string OnlineChangelog => "## 0.3-dev-13 · 2026-11-01\n\n### Featured\n- **Something new**, with a long line\n  that wraps onto the next one\n\n" + UpdateService.BundledChangelog();

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
            && !UpdateService.IsNewer("0.1-dev-3", "0.1-dev-3") && !UpdateService.IsNewer("0.1-dev-10", "0.3-dev-12") && UpdateService.ParseVersion("Alpha-01") == null,
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
        Check(releases.Select(r => r.Version).SequenceEqual(new[] { "0.3-dev-13", "0.1-dev-1" }) && UpdateService.NewerThan(releases, "0.3-dev-12")?.Version == "0.3-dev-13"
            && UpdateService.NewerThan(releases, "0.3-dev-13") == null && http.DefaultRequestHeaders.UserAgent.ToString().StartsWith("Keyloom/"),
            "update check finds the newest published release");
        using var down = UpdateService.CreateClient(new FakeHttp(_ => throw new HttpRequestException("offline")));
        Check(UpdateService.FetchReleasesAsync(down).GetAwaiter().GetResult() == null && UpdateService.FetchChangelogAsync(down).GetAwaiter().GetResult() == null,
            "being offline is reported as no answer, not an error");
        using var limited = UpdateService.CreateClient(new FakeHttp(_ => Text("{\"message\":\"rate limited\"}", HttpStatusCode.Forbidden)));
        Check(UpdateService.FetchReleasesAsync(limited).GetAwaiter().GetResult() == null, "GitHub errors are treated like being offline");

        // About page: online it shows the newer version and the GitHub changelog; offline it keeps the bundled one.
        AboutView? about = null;
        RunOnDispatcher(() => { about = new AboutView(http, "0.3-dev-12"); return about.CheckAsync(); });
        var entries = ((IEnumerable<ChangelogEntryView>)((ItemsControl)about!.FindName("ChangelogList")).ItemsSource).ToList();
        Check(((TextBlock)about.FindName("StatusText")).Text.Contains("0.3-dev-13 is out") && ((FrameworkElement)about.FindName("DownloadButton")).Visibility == Visibility.Visible
            && ((TextBlock)about.FindName("SourceText")).Text.Contains("GitHub"), "about shows a newer release with a download button");
        Check(entries[0] is { Version: "0.3-dev-13", IsNew: true } && entries[1] is { Version: "0.3-dev-12", IsInstalled: true, IsNew: false }, "about marks new and installed versions in the changelog");
        RunOnDispatcher(() => { about = new AboutView(down, "0.3-dev-12"); return about.CheckAsync(); });
        entries = ((IEnumerable<ChangelogEntryView>)((ItemsControl)about!.FindName("ChangelogList")).ItemsSource).ToList();
        Check(((TextBlock)about.FindName("StatusText")).Text.Contains("Couldn't reach GitHub") && ((TextBlock)about.FindName("SourceText")).Text.Contains("Included")
            && entries[0].IsInstalled && ((Button)about.FindName("CheckButton")).IsEnabled, "offline, about keeps the bundled changelog and lets you try again");
        using var current = UpdateService.CreateClient(new FakeHttp(r => r.RequestUri!.Host == "api.github.com" ? Text(ReleasesJson) : Text(UpdateService.BundledChangelog())));
        RunOnDispatcher(() => { about = new AboutView(current, "0.3-dev-13"); return about.CheckAsync(); });
        Check(((TextBlock)about!.FindName("StatusText")).Text.Contains("latest version") && ((FrameworkElement)about.FindName("DownloadButton")).Visibility == Visibility.Collapsed,
            "about says when you have the latest version");
    }
}
