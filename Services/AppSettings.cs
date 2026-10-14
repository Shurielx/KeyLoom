using System.IO;
using System.Reflection;
using System.Text.Json;

namespace Keyloom.Services;

/// <summary>App-wide preferences, separate from macro profiles. Missing or unreadable files fall back to defaults.</summary>
public sealed class AppSettings
{
    public bool ShowExperimental { get; set; }
    public AppTheme Theme { get; set; }

    public static AppSettings Current { get; private set; } = new();
    private static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keyloom", "settings.json");
    public static AppSettings Load(string? path = null)
    {
        try
        {
            path ??= DefaultPath;
            Current = File.Exists(path) && new FileInfo(path).Length < 64 * 1024
                ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new() : new();
            if (!Enum.IsDefined(Current.Theme)) Current.Theme = AppTheme.System;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Current = new(); }
        return Current;
    }
    public AppSettings With(Action<AppSettings> change)
    {
        var next = new AppSettings { ShowExperimental = ShowExperimental, Theme = Theme };
        change(next); return next;
    }
    public void Save(string? path = null)
    {
        path ??= DefaultPath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        Current = this;
    }
}

public static class AppVersion
{
    /// <summary>Release label as shown to people, without build metadata: "0.1-dev-1+abc" becomes "0.1-dev-1".</summary>
    public static string Format(string version) => version.Split('+')[0];
    public static string Display => Format(typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
}
