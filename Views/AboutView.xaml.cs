using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>A changelog entry as the About page shows it.</summary>
public sealed record ChangelogEntryView(string Version, string Date, IReadOnlyList<ChangelogSection> Sections, bool IsInstalled, bool IsNew);

/// <summary>
/// About Keyloom: this version, whether a newer one is out, and the changelog. The bundled changelog shows at once;
/// the page then asks GitHub for the newest changelog and releases, and keeps the bundled copy when offline.
/// A newer version installs from here: it downloads, then Keyloom restarts into it.
/// </summary>
public partial class AboutView : UserControl
{
    private readonly HttpClient _http;
    private readonly Func<string, Task>? _restart;
    private readonly string _current;
    private CancellationTokenSource? _cts;
    /// <summary>The release the main button installs: a newer one, or the current one when Keyloom is not installed yet.</summary>
    private ReleaseInfo? _offer;
    /// <summary>Finishes when the latest check has finished; lets tests wait for it.</summary>
    public Task Checking { get; private set; } = Task.CompletedTask;

    /// <param name="restart">Installs the downloaded Keyloom.exe and restarts into it. Without it, the button opens the release page.</param>
    public AboutView(HttpClient? http = null, string? currentVersion = null, Func<string, Task>? restart = null)
    {
        InitializeComponent();
        L.Apply(this);
        _http = http ?? UpdateService.Shared;
        _restart = restart;
        _current = currentVersion ?? AppVersion.Display;
        VersionText.Text = _current;
        ShowChangelog(UpdateService.BundledChangelog(), ChangelogSource.Bundled);
        Loaded += (_, _) => { if (_cts == null) Checking = CheckAsync(); };
        Unloaded += (_, _) => { _cts?.Cancel(); _cts = null; };
    }

    private void ShowChangelog(string markdown, ChangelogSource source)
    {
        ChangelogList.ItemsSource = UpdateService.ParseChangelog(markdown)
            .Select(e => new ChangelogEntryView(e.Version, e.Date, e.Sections, e.Version == _current, UpdateService.IsNewer(e.Version, _current)))
            .ToList();
        SourceText.Text = source == ChangelogSource.GitHub ? L.T("Latest from GitHub") : L.T("Included with this version");
    }

    /// <summary>Fetches the newest changelog and releases together, then shows what it found.</summary>
    public async Task CheckAsync()
    {
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        CheckButton.IsEnabled = false;
        SetStatus(L.T("Checking GitHub for a newer version..."), StatusKind.Working);
        var changelog = UpdateService.FetchChangelogAsync(_http, cts.Token);
        var releases = UpdateService.FetchReleasesAsync(_http, cts.Token);
        string? online = await changelog;
        var found = await releases;
        if (cts.IsCancellationRequested) return;
        CheckButton.IsEnabled = true;
        if (online != null) ShowChangelog(online, ChangelogSource.GitHub);
        if (found == null)
        {
            SetStatus(L.T("Couldn't reach GitHub. The changelog below came with this version."), StatusKind.Offline);
            return;
        }
        var newer = UpdateService.NewerThan(found, _current);
        bool canInstall = _restart != null;
        if (newer != null)
        {
            _offer = newer;
            SetStatus(newer.IsPrerelease ? L.T($"Keyloom {newer.Version} is out (development build).") : L.T($"Keyloom {newer.Version} is out."), StatusKind.Available);
            DownloadText.Text = canInstall && newer.DownloadUrl != null ? L.T($"Update to {newer.Version}") : L.T($"Get {newer.Version}");
        }
        else if (canInstall && !UpdateService.IsInstalled && found.FirstOrDefault(r => r.Version == _current && r.DownloadUrl != null) is { } same)
        {
            // Started from a ZIP or a build folder: offer the same version in its own folder, found by the Start menu and updated from here.
            _offer = same;
            SetStatus(L.T("You have the latest version. Install it to open Keyloom from the Start menu and update it with one click."), StatusKind.Available);
            DownloadText.Text = L.T("Install");
        }
        else { _offer = null; SetStatus(L.T("You have the latest version."), StatusKind.Latest); }
    }

    /// <summary>Downloads the offered release and hands it to the restart, which closes this Keyloom and opens the new one.</summary>
    public async Task UpdateAsync()
    {
        if (_offer is not { DownloadUrl: not null } offer || _restart == null) { Open(_offer?.Url ?? UpdateService.ReleasesUrl); return; }
        _cts?.Cancel();
        var cts = _cts = new CancellationTokenSource();
        CheckButton.IsEnabled = DownloadButton.IsEnabled = false;
        string downloading = L.T($"Downloading Keyloom {offer.Version}...");
        SetStatus(downloading, StatusKind.Working);
        DownloadButton.Visibility = Visibility.Visible;
        var progress = new Progress<double>(f => { if (!cts.IsCancellationRequested) StatusText.Text = $"{downloading} {(int)(f * 100)}%"; });
        try
        {
            string exe = await UpdateService.DownloadAsync(_http, offer, progress, token: cts.Token);
            if (cts.IsCancellationRequested) return;
            StatusText.Text = L.T("Restarting Keyloom...");
            await _restart(exe);
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            SetStatus(L.T($"The update didn't finish: {ex.Message}"), StatusKind.Offline);
            DownloadButton.Visibility = Visibility.Visible;
        }
        catch (OperationCanceledException) { return; }
        finally { CheckButton.IsEnabled = DownloadButton.IsEnabled = true; }
    }

    private enum StatusKind { Working, Latest, Available, Offline }
    private void SetStatus(string text, StatusKind kind)
    {
        StatusText.Text = text;
        StatusCheck.Visibility = kind == StatusKind.Latest ? Visibility.Visible : Visibility.Collapsed;
        StatusDot.Visibility = kind == StatusKind.Latest ? Visibility.Collapsed : Visibility.Visible;
        StatusDot.SetResourceReference(Shape.FillProperty, kind switch
        {
            StatusKind.Available => "AccentBrush", StatusKind.Offline => "DangerIconBrush", _ => "MutedBrush"
        });
        StatusText.SetResourceReference(TextBlock.ForegroundProperty, kind == StatusKind.Available ? "InkBrush" : "MutedBrush");
        StatusText.FontWeight = kind == StatusKind.Available ? FontWeights.SemiBold : FontWeights.Normal;
        DownloadButton.Visibility = kind == StatusKind.Available ? Visibility.Visible : Visibility.Collapsed;
        // A soft pulse while checking, so the page does not look stuck.
        StatusDot.BeginAnimation(OpacityProperty, kind == StatusKind.Working
            ? new DoubleAnimation(1, .3, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever }
            : null);
        StatusPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(.2, 1, TimeSpan.FromMilliseconds(220)));
    }

    private void Check_Click(object sender, RoutedEventArgs e) => Checking = CheckAsync();
    private void Download_Click(object sender, RoutedEventArgs e) => Checking = UpdateAsync();
    private void Releases_Click(object sender, RoutedEventArgs e) => Open(UpdateService.ReleasesUrl);
    private void Open(string url)
    {
        try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            SetStatus(L.T($"Couldn't open the browser. The releases are at {UpdateService.ReleasesUrl}"), StatusKind.Offline);
        }
    }
}

/// <summary>Shows **bold** and `code` from changelog text in a TextBlock.</summary>
public static partial class RichText
{
    public static readonly DependencyProperty MarkdownProperty = DependencyProperty.RegisterAttached("Markdown", typeof(string), typeof(RichText),
        new PropertyMetadata(null, (d, e) => { if (d is TextBlock block) Fill(block, e.NewValue as string ?? ""); }));
    public static string GetMarkdown(DependencyObject d) => (string)d.GetValue(MarkdownProperty);
    public static void SetMarkdown(DependencyObject d, string value) => d.SetValue(MarkdownProperty, value);

    [GeneratedRegex(@"(\*\*[^*]+\*\*|`[^`]+`)")]
    private static partial Regex Marks();
    private static void Fill(TextBlock block, string text)
    {
        block.Inlines.Clear();
        foreach (string part in Marks().Split(text))
        {
            if (part.Length == 0) continue;
            if (part.StartsWith("**") && part.EndsWith("**") && part.Length > 4)
            {
                var bold = new Run(part[2..^2]) { FontWeight = FontWeights.SemiBold };
                bold.SetResourceReference(TextElement.ForegroundProperty, "InkBrush");
                block.Inlines.Add(bold);
            }
            else if (part.StartsWith('`') && part.EndsWith('`') && part.Length > 2)
                block.Inlines.Add(new Run(part[1..^1]) { FontFamily = new FontFamily("Cascadia Mono, Consolas") , FontSize = 11.5 });
            else block.Inlines.Add(new Run(part));
        }
    }
}
