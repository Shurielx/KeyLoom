using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keyloom.Engine;

namespace Keyloom.Services;

/// <summary>App-wide preferences, separate from macro profiles. Missing or unreadable files fall back to defaults.</summary>
public sealed class AppSettings
{
    public bool ShowExperimental { get; set; }
    /// <summary>Experimental: shows Build on screen in the editor.</summary>
    public bool MacroBuilder { get; set; }
    /// <summary>Experimental: adds Script, macros written in JavaScript, to the editor.</summary>
    public bool Scripts { get; set; }
    /// <summary>Experimental: lets macros ask an AI about the screen. The AI only answers; it never clicks or types.</summary>
    public bool Ai { get; set; }
    public AiProvider AiProvider { get; set; }
    public string AiModel { get; set; } = "";
    public string OllamaUrl { get; set; } = AiVision.DefaultOllamaUrl;
    /// <summary>The OpenRouter key, encrypted for this Windows user, so the settings file alone does not give it away.</summary>
    public string OpenRouterKey { get; set; } = "";
    /// <summary>How macros reach the AI right now, or null while AI is off.</summary>
    [JsonIgnore] public AiSetup? AiSetup => Ai ? new(AiProvider, AiModel.Trim(), OllamaUrl.Trim(), Secret.Reveal(OpenRouterKey)) : null;
    public AppTheme Theme { get; set; }
    /// <summary>A language code, or empty to follow Windows when Keyloom has its language. A change shows after a restart.</summary>
    public string Language { get; set; } = "";
    /// <summary>Keeps a small "running" card on screen while a macro runs.</summary>
    public bool ShowRunIndicator { get; set; } = true;
    public IndicatorPlace IndicatorPlace { get; set; }
    /// <summary>A quiet sound when Keyloom's own buttons are clicked.</summary>
    public bool ClickSounds { get; set; } = true;

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
            if (!Enum.IsDefined(Current.IndicatorPlace)) Current.IndicatorPlace = IndicatorPlace.Top;
            Current.Language ??= "";
            if (!Enum.IsDefined(Current.AiProvider)) Current.AiProvider = AiProvider.Ollama;
            Current.AiModel ??= ""; Current.OpenRouterKey ??= "";
            if (string.IsNullOrWhiteSpace(Current.OllamaUrl)) Current.OllamaUrl = AiVision.DefaultOllamaUrl;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { Current = new(); }
        return Current;
    }
    public AppSettings With(Action<AppSettings> change)
    {
        var next = new AppSettings { ShowExperimental = ShowExperimental, MacroBuilder = MacroBuilder, Scripts = Scripts, Ai = Ai, AiProvider = AiProvider, AiModel = AiModel, OllamaUrl = OllamaUrl, OpenRouterKey = OpenRouterKey, Theme = Theme, Language = Language, ShowRunIndicator = ShowRunIndicator, IndicatorPlace = IndicatorPlace, ClickSounds = ClickSounds };
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

/// <summary>Where the running indicator sits on the screen with the pointer.</summary>
public enum IndicatorPlace { Top, TopRight, BottomRight }

/// <summary>Keeps a secret, like an API key, encrypted for the signed-in Windows user.</summary>
public static class Secret
{
    public static string Hide(string text) => text.Length == 0 ? "" :
        Convert.ToBase64String(System.Security.Cryptography.ProtectedData.Protect(System.Text.Encoding.UTF8.GetBytes(text), null, System.Security.Cryptography.DataProtectionScope.CurrentUser));
    /// <summary>The secret, or empty when it cannot be read, such as one copied from another user or PC.</summary>
    public static string Reveal(string hidden)
    {
        if (hidden.Length == 0) return "";
        try { return System.Text.Encoding.UTF8.GetString(System.Security.Cryptography.ProtectedData.Unprotect(Convert.FromBase64String(hidden), null, System.Security.Cryptography.DataProtectionScope.CurrentUser)); }
        catch (Exception ex) when (ex is FormatException or System.Security.Cryptography.CryptographicException) { return ""; }
    }
}

public static class AppVersion
{
    /// <summary>Release label as shown to people, without build metadata: "0.1-dev-1+abc" becomes "0.1-dev-1".</summary>
    public static string Format(string version) => version.Split('+')[0];
    public static string Display => Format(typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0");
}
