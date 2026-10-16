using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>One macro's line in the usage list. Share and Rest split the bar by run time.</summary>
public sealed record UsageRow(MacroProfile Profile, string Totals, string Last, GridLength Share, GridLength Rest);

/// <summary>App preferences and usage statistics. Changes apply and save immediately.</summary>
public partial class SettingsView : UserControl
{
    private readonly string? _path;
    private readonly IReadOnlyList<MacroProfile> _profiles;
    private readonly UsageStore? _usage;
    private bool _loading = true;
    public SettingsView(string? path = null, IReadOnlyList<MacroProfile>? profiles = null, UsageStore? usage = null)
    {
        InitializeComponent();
        L.Apply(this);
        _path = path; _profiles = profiles ?? []; _usage = usage;
        var settings = AppSettings.Current;
        ExperimentalBox.IsChecked = settings.ShowExperimental;
        BuilderBox.IsChecked = settings.MacroBuilder;
        ScriptsBox.IsChecked = settings.Scripts;
        AiBox.IsChecked = settings.Ai;
        (settings.AiProvider == Engine.AiProvider.OpenRouter ? OpenRouterRadio : OllamaRadio).IsChecked = true;
        OllamaUrlTextBox.Text = settings.OllamaUrl;
        OpenRouterKeyBox.Password = Secret.Reveal(settings.OpenRouterKey);
        AiModelBox.Text = settings.AiModel;
        ShowAi();
        (settings.Theme switch { AppTheme.Light => LightThemeRadio, AppTheme.Dark => DarkThemeRadio, _ => SystemThemeRadio }).IsChecked = true;
        IndicatorBox.IsChecked = settings.ShowRunIndicator;
        SoundBox.IsChecked = settings.ClickSounds;
        LanguagePanel.Visibility = L.Available.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
        LanguageBox.ItemsSource = new[] { new KeyValuePair<string, string>("", L.T("Same as Windows")) }.Concat(L.Available.Select(c => new KeyValuePair<string, string>(c, L.NameOf(c)))).ToList();
        LanguageBox.SelectedValue = L.Available.Contains(settings.Language) ? settings.Language : "";
        (settings.IndicatorPlace switch { IndicatorPlace.TopRight => PlaceTopRightRadio, IndicatorPlace.BottomRight => PlaceBottomRightRadio, _ => PlaceTopRadio }).IsChecked = true;
        VersionText.Text = $"Keyloom {AppVersion.Display}";
        _loading = false;
        ShowUsage();
        if (_usage != null)
        {
            _usage.Changed += ShowUsage;
            Unloaded += (_, _) => _usage.Changed -= ShowUsage;
        }
    }
    private void Theme_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var theme = sender == LightThemeRadio ? AppTheme.Light : sender == DarkThemeRadio ? AppTheme.Dark : AppTheme.System;
        ThemeManager.Apply(theme);
        Save(s => s.Theme = theme);
    }
    private void Experimental_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Save(s => s.ShowExperimental = ExperimentalBox.IsChecked == true);
    }
    private void Scripts_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Save(s => s.Scripts = ScriptsBox.IsChecked == true);
    }
    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && LanguageBox.SelectedValue is string code) Save(s => s.Language = code);
    }
    private void Ai_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Save(s => s.Ai = AiBox.IsChecked == true);
        ShowAi();
    }
    private Engine.AiProvider PickedProvider => OpenRouterRadio.IsChecked == true ? Engine.AiProvider.OpenRouter : Engine.AiProvider.Ollama;
    private void ShowAi()
    {
        AiPanel.Visibility = AiBox.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        bool online = PickedProvider == Engine.AiProvider.OpenRouter;
        OllamaPanel.Visibility = online ? Visibility.Collapsed : Visibility.Visible;
        OpenRouterPanel.Visibility = online ? Visibility.Visible : Visibility.Collapsed;
        AiProviderHint.Text = online
            ? L.T("Reaches models like Claude and Gemini over the internet. What the AI is shown, a picture of the window or screen, goes to OpenRouter and the model's maker, and each question costs a little.")
            : L.T("Runs models on your PC, free and offline: nothing leaves it. Install Ollama from ollama.com and get a model that can see, such as: ollama pull gemma3");
    }
    private void AiProvider_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        // Model names differ between the two, so a model from the other one is not kept.
        AiModelBox.ItemsSource = null; AiModelBox.Text = "";
        Save(s => { s.AiProvider = PickedProvider; s.AiModel = ""; });
        AiStatusText.Text = "";
        ShowAi();
    }
    private void AiModel_Picked(object sender, SelectionChangedEventArgs e)
    {
        if (!_loading && AiModelBox.SelectedItem is string model) Save(s => s.AiModel = model);
    }
    private void AiField_Changed(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        string url = OllamaUrlTextBox.Text.Trim(), key = OpenRouterKeyBox.Password.Trim(), model = AiModelBox.Text.Trim();
        if (url.Length > 0 && !Uri.TryCreate(url, UriKind.Absolute, out _)) { AiStatusText.Text = L.T("The Ollama address should look like http://localhost:11434."); return; }
        Save(s =>
        {
            s.OllamaUrl = url.Length > 0 ? url : Engine.AiVision.DefaultOllamaUrl;
            s.OpenRouterKey = Secret.Hide(key);
            s.AiModel = model;
        });
    }
    private Engine.AiSetup TypedSetup => new(PickedProvider, AiModelBox.Text.Trim(), OllamaUrlTextBox.Text.Trim(), OpenRouterKeyBox.Password.Trim());
    private async void LoadModels_Click(object sender, RoutedEventArgs e)
    {
        AiStatusText.Text = L.T("Loading models...");
        try
        {
            var models = await Engine.AiVision.ModelsAsync(TypedSetup, AiHttp.Value);
            string typed = AiModelBox.Text;
            AiModelBox.ItemsSource = models;
            AiModelBox.Text = typed;
            AiStatusText.Text = models.Count == 0
                ? PickedProvider == Engine.AiProvider.Ollama ? L.T("Ollama has no models yet. Get one that can see, such as: ollama pull gemma3") : L.T("No models that can look at pictures were found.")
                : L.N(models.Count, "{0} model can look at pictures. Pick it from the list.", "{0} models can look at pictures. Pick one from the list.");
            if (models.Count > 0) AiModelBox.IsDropDownOpen = true;
        }
        catch (InvalidOperationException ex) { AiStatusText.Text = ex.Message; }
    }
    /// <summary>Asks the model a short question about a small picture, to show that the setup works.</summary>
    private async void AiTest_Click(object sender, RoutedEventArgs e)
    {
        AiField_Changed(sender, e);
        var setup = TypedSetup;
        AiStatusText.Text = L.T($"Asking {setup.Model}...");
        AiTestButton.IsEnabled = false;
        try
        {
            // A plain blue square: easy to answer, and nothing from the screen is sent.
            var blue = new Engine.PixelImage(64, 64, Enumerable.Repeat(0xFF2060E0u, 64 * 64).ToArray());
            var clock = Stopwatch.StartNew();
            string answer = await Task.Run(() => new Engine.AiVision(setup, AiHttp.Value).Ask("What color is this picture? Answer with one word.", blue, CancellationToken.None));
            AiStatusText.Text = L.T($"It works. Asked what color a blue square is, {setup.Model} said \"{answer}\" in {clock.Elapsed.TotalSeconds:N1} s.");
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException) { AiStatusText.Text = ex.Message; }
        finally { AiTestButton.IsEnabled = true; }
    }
    private static readonly Lazy<System.Net.Http.HttpClient> AiHttp = new(() => Engine.AiVision.Client());
    private void Builder_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Save(s => s.MacroBuilder = BuilderBox.IsChecked == true);
    }
    private void Indicator_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Save(s => s.ShowRunIndicator = IndicatorBox.IsChecked == true);
    }
    private void Sound_Changed(object sender, RoutedEventArgs e)
    {
        if (!_loading) Save(s => s.ClickSounds = SoundBox.IsChecked == true);
    }
    private void Place_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        var place = sender == PlaceTopRightRadio ? IndicatorPlace.TopRight : sender == PlaceBottomRightRadio ? IndicatorPlace.BottomRight : IndicatorPlace.Top;
        Save(s => s.IndicatorPlace = place);
    }
    private void Save(Action<AppSettings> change)
    {
        try { AppSettings.Current.With(change).Save(_path); ErrorText.Text = ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { ErrorText.Text = L.T($"Settings could not be saved: {ex.Message}"); }
    }

    private void ShowUsage()
    {
        var usage = _usage ?? new UsageStore();
        var used = _profiles.Select(p => (Profile: p, Usage: usage.For(p.Id))).Where(x => x.Usage.Starts > 0)
            .OrderByDescending(x => x.Usage.RunMs).ThenByDescending(x => x.Usage.Starts).ToList();
        long total = Math.Max(1, used.Sum(x => x.Usage.RunMs));
        StartsText.Text = usage.TotalStarts.ToString("N0");
        RunTimeText.Text = UsageStore.Duration(usage.TotalRunMs);
        TopText.Text = used.Count == 0 ? L.T("None yet") : used.MaxBy(x => x.Usage.Starts).Profile.Name;
        UsageList.ItemsSource = used.Select(x =>
        {
            double share = (double)x.Usage.RunMs / total;
            string cycles = x.Usage.Cycles > 0 && x.Profile.Behavior != MacroBehaviorType.PressAndHold ? $" · {x.Usage.Cycles:N0} {x.Profile.CycleNoun}{(x.Usage.Cycles == 1 ? "" : "s")}" : "";
            return new UsageRow(x.Profile, $"{x.Usage.Starts:N0}× · {UsageStore.Duration(x.Usage.RunMs)}{cycles}", "Last " + UsageStore.When(x.Usage.LastRun),
                new GridLength(Math.Max(.01, share), GridUnitType.Star), new GridLength(Math.Max(.01, 1 - share), GridUnitType.Star));
        }).ToList();
        UsageList.Visibility = used.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        SinceText.Text = used.Count == 0 ? L.T("Start a macro and it shows up here: how often it ran and for how long.") : L.T($"Counting since {usage.Since.ToString("d MMM yyyy", System.Globalization.CultureInfo.CurrentCulture)}. Test runs from the editor don't count.");
        ResetUsageButton.IsEnabled = used.Count > 0;
    }
    private async void ResetUsage_Click(object sender, RoutedEventArgs e)
    {
        if (_usage == null) return;
        if (SheetLayer.For(this) is { } sheets && !await sheets.ConfirmAsync(L.T("Reset usage?"), L.T("Run counts and times start again from zero. Your macros stay as they are."), L.T("Reset"), danger: true)) return;
        _usage.Reset();
    }
    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        string folder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keyloom");
        try { Directory.CreateDirectory(folder); Process.Start(new ProcessStartInfo("explorer.exe", $"\"{folder}\"") { UseShellExecute = true }); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception) { ErrorText.Text = ex.Message; }
    }
}
