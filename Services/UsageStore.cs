using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Keyloom.Services;

/// <summary>How much one macro has been used, kept apart from the macro itself so editing never touches it.</summary>
public sealed class MacroUsage
{
    /// <summary>Times the macro was started.</summary>
    public long Starts { get; set; }
    /// <summary>Repeats played: clicks for a repeat macro, runs for a sequence.</summary>
    public long Cycles { get; set; }
    public long RunMs { get; set; }
    public DateTime? LastRun { get; set; }
}

/// <summary>Usage statistics per macro, stored in usage.json next to the macros. A missing or unreadable file starts empty.</summary>
public sealed class UsageStore
{
    public Dictionary<string, MacroUsage> Macros { get; set; } = new();
    /// <summary>When counting started, or was last reset.</summary>
    public DateTime Since { get; set; } = DateTime.Now;
    [JsonIgnore] public string? Path { get; private set; }
    public event Action? Changed;
    private static string DefaultPath => System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keyloom", "usage.json");
    public static UsageStore Load(string? path = null)
    {
        path ??= DefaultPath;
        UsageStore store;
        try
        {
            store = File.Exists(path) && new FileInfo(path).Length < 4 * 1024 * 1024
                ? JsonSerializer.Deserialize<UsageStore>(File.ReadAllText(path)) ?? new() : new();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { store = new(); }
        store.Macros ??= new();
        store.Path = path;
        return store;
    }
    /// <summary>Writes the file. Statistics are a nice-to-have, so a failed write is ignored.</summary>
    public void Save()
    {
        if (Path == null) return;
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
            File.WriteAllText(Path, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
    public MacroUsage For(string id) => Macros.TryGetValue(id, out var usage) ? usage : new();
    public void Started(string id)
    {
        var usage = Macros.TryGetValue(id, out var found) ? found : Macros[id] = new();
        usage.Starts++; usage.LastRun = DateTime.Now;
        Changed?.Invoke();
    }
    public void Stopped(string id, TimeSpan ran, long cycles)
    {
        if (!Macros.TryGetValue(id, out var usage)) return;
        usage.RunMs += (long)Math.Max(0, ran.TotalMilliseconds);
        usage.Cycles += Math.Max(0, cycles);
        Save(); Changed?.Invoke();
    }
    /// <summary>Drops statistics of macros that no longer exist.</summary>
    public void Keep(IEnumerable<string> ids)
    {
        var keep = ids.ToHashSet();
        foreach (var id in Macros.Keys.Where(id => !keep.Contains(id)).ToList()) Macros.Remove(id);
    }
    public void Reset()
    {
        Macros.Clear(); Since = DateTime.Now;
        Save(); Changed?.Invoke();
    }
    public long TotalStarts => Macros.Values.Sum(u => u.Starts);
    public long TotalRunMs => Macros.Values.Sum(u => u.RunMs);

    /// <summary>A duration the way people say it: 45 s, 12 min, 3 h 5 min.</summary>
    public static string Duration(long ms)
    {
        var t = TimeSpan.FromMilliseconds(Math.Max(0, ms));
        if (t.TotalSeconds < 60) return $"{(int)t.TotalSeconds} s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes} min";
        return t.Minutes == 0 ? $"{(int)t.TotalHours} h" : L.T($"{(int)t.TotalHours} h {t.Minutes} min");
    }
    /// <summary>A running clock: 0:07, 12:30, 1:02:03.</summary>
    public static string Clock(TimeSpan t) => t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t:mm\\:ss}" : $"{(int)t.TotalMinutes}:{t:ss}";
    /// <summary>When something last happened: today 14:02, yesterday, 3 days ago, 12 Oct.</summary>
    public static string When(DateTime? time, DateTime? now = null)
    {
        if (time is not { } t) return "never";
        var today = (now ?? DateTime.Now).Date;
        int days = (today - t.Date).Days;
        return days switch
        {
            <= 0 => L.T($"today {t:HH:mm}"),
            1 => L.T("yesterday"),
            < 7 => L.N(days, "{0} day ago", "{0} days ago"),
            _ => t.ToString("d MMM", System.Globalization.CultureInfo.CurrentCulture)
        };
    }
}
