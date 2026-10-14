using System.Windows;
using Microsoft.Win32;

namespace Keyloom.Services;

public enum AppTheme { System, Light, Dark }

/// <summary>Switches the color palette live. Light colors are the base; the dark palette is layered on top of it.</summary>
public static class ThemeManager
{
    private static readonly Uri DarkSource = new("/Keyloom;component/Assets/Colors.Dark.xaml", UriKind.Relative);
    private static ResourceDictionary? _dark;
    private static AppTheme _theme;
    private static bool _listening;
    public static bool IsDark { get; private set; }
    public static event Action? Changed;
    public static void Apply(AppTheme theme)
    {
        _theme = theme;
        if (!_listening && theme == AppTheme.System)
        {
            _listening = true;
            SystemEvents.UserPreferenceChanged += (_, e) =>
            {
                if (e.Category == UserPreferenceCategory.General && _theme == AppTheme.System)
                    Application.Current?.Dispatcher.BeginInvoke(() => Apply(AppTheme.System));
            };
        }
        bool dark = theme == AppTheme.Dark || (theme == AppTheme.System && SystemPrefersDark());
        if (Application.Current is not { } app) { IsDark = dark; return; }
        var merged = app.Resources.MergedDictionaries;
        _dark ??= new ResourceDictionary { Source = DarkSource };
        bool present = merged.Contains(_dark);
        if (dark && !present) merged.Add(_dark);
        else if (!dark && present) merged.Remove(_dark);
        if (IsDark == dark && present == dark) return;
        IsDark = dark;
        Changed?.Invoke();
    }
    public static bool SystemPrefersDark()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException) { return false; }
    }
}
