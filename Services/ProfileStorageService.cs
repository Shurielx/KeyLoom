using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Services;

public sealed record ProfileLoadResult(ObservableCollection<MacroProfile> Profiles, string Warning);

public sealed class ProfileStorageService
{
    public string FilePath { get; }
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, PropertyNameCaseInsensitive = true };
    public ProfileStorageService(string? folder = null)
    {
        string appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        FilePath = Path.Combine(folder ?? Path.Combine(appData, "Keyloom"), "macro_profiles.json");
    }
    public ProfileLoadResult LoadProfiles()
    {
        string source = FilePath;
        if (!File.Exists(source)) return new(new() { CreateDefault() }, "");
        try { return new(Read(source), ""); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            try { return new(Read(source + ".bak"), "Profiles recovered from backup. The previous file could not be read."); }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                return new(new() { CreateDefault() }, "Profiles could not be loaded. Your existing file is preserved until you save.");
            }
        }
    }
    private static ObservableCollection<MacroProfile> Read(string path)
    {
        if (new FileInfo(path).Length > 8 * 1024 * 1024) throw new JsonException("Profile file is too large.");
        var profiles = JsonSerializer.Deserialize<ObservableCollection<MacroProfile>>(File.ReadAllText(path), Options) ?? throw new JsonException("Missing profiles.");
        var ids = new HashSet<string>();
        foreach (var p in profiles)
        {
            if (p == null) throw new JsonException("Missing profile.");
            if (string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id)) { p.Id = Guid.NewGuid().ToString("N"); ids.Add(p.Id); }
            p.SpamIntervalMs = Math.Max(1, p.SpamIntervalMs);
            ProfileValidator.Validate(p);
        }
        return profiles;
    }
    public void SaveProfiles(IEnumerable<MacroProfile> profiles)
    {
        var snapshot = profiles.Select(p => p.Snapshot()).ToArray();
        foreach (var p in snapshot) ProfileValidator.Validate(p);
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        string temporary = FilePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, snapshot, Options);
                if (stream.Length > 8 * 1024 * 1024) throw new IOException("Profiles exceed the 8 MB storage limit. Shorten large recordings or text steps.");
                stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static MacroProfile CreateDefault() => new() { Name = "Autoclicker", PrimaryVk = 0x77, PrimaryModifiers = Shortcut.Control | Shortcut.Shift };
}
