using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Services;

public sealed record ProfileLoadResult(ObservableCollection<MacroProfile> Profiles, string Warning);

/// <summary>
/// Keeps every macro in macros.keyloom (see <see cref="MacroFile"/>), with a backup of the previous save beside it.
/// Macros saved by Keyloom 0.2 and older in macro_profiles.json move over on the first start; that file is left as it was.
/// </summary>
public sealed class ProfileStorageService
{
    public string FilePath { get; }
    /// <summary>Where Keyloom 0.2 and older kept macros.</summary>
    public string LegacyPath { get; }
    private const long MaxBytes = 8 * 1024 * 1024;
    private static readonly JsonSerializerOptions LegacyOptions = new() { PropertyNameCaseInsensitive = true };
    public ProfileStorageService(string? folder = null)
    {
        folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Keyloom");
        FilePath = Path.Combine(folder, "macros" + MacroFile.Extension);
        LegacyPath = Path.Combine(folder, "macro_profiles.json");
    }
    public ProfileLoadResult LoadProfiles()
    {
        if (File.Exists(FilePath)) return Load(FilePath, Read).Result;
        if (!File.Exists(LegacyPath) && !File.Exists(LegacyPath + ".bak")) return new(new() { CreateDefault() }, "");
        var (result, failed) = Load(LegacyPath, ReadLegacy);
        // Nothing readable: keep trying the old file next time instead of saving the default macro over it.
        if (failed) return result;
        try { SaveProfiles(result.Profiles); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return result with { Warning = "Your macros were loaded but could not be saved in the new format yet: " + ex.Message };
        }
        return result;
    }
    private static (ProfileLoadResult Result, bool Failed) Load(string path, Func<string, ObservableCollection<MacroProfile>> read)
    {
        try { return (new(read(path), ""), false); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
        {
            try { return (new(read(path + ".bak"), $"Macros recovered from the backup. The last save could not be read: {ex.Message}"), false); }
            catch (Exception backupError) when (backupError is IOException or UnauthorizedAccessException or JsonException or ArgumentException)
            {
                return (new(new() { CreateDefault() }, $"Macros could not be loaded: {ex.Message} Your file is kept until you save."), true);
            }
        }
    }
    private static ObservableCollection<MacroProfile> Read(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new JsonException("The macro file is larger than 8 MB.");
        return new(MacroFile.Read(File.ReadAllText(path)));
    }
    private static ObservableCollection<MacroProfile> ReadLegacy(string path)
    {
        if (new FileInfo(path).Length > MaxBytes) throw new JsonException("The macro file is larger than 8 MB.");
        var profiles = JsonSerializer.Deserialize<ObservableCollection<MacroProfile>>(File.ReadAllText(path), LegacyOptions) ?? throw new JsonException("Missing profiles.");
        var ids = new HashSet<string>();
        foreach (var p in profiles)
        {
            if (p == null) throw new JsonException("Missing profile.");
            if (string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id)) { p.Id = Guid.NewGuid().ToString("N"); ids.Add(p.Id); }
            p.SpamIntervalMs = Math.Max(1, p.SpamIntervalMs);
            if (p.FallbackVk != 0 && p.PrimaryVk == 0) { p.PrimaryVk = p.FallbackVk; p.PrimaryModifiers = p.FallbackModifiers; }
            p.FallbackVk = 0; p.FallbackModifiers = 0;
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
            using (var writer = new StreamWriter(stream))
            {
                writer.Write(MacroFile.Write(snapshot));
                writer.Flush();
                if (stream.Length > MaxBytes) throw new IOException("Macros exceed the 8 MB storage limit. Shorten large recordings or text steps.");
                stream.Flush(true);
            }
            if (File.Exists(FilePath)) File.Replace(temporary, FilePath, FilePath + ".bak");
            else File.Move(temporary, FilePath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    public static MacroProfile CreateDefault() => new() { Name = "Autoclicker", PrimaryVk = 0x77, PrimaryModifiers = Shortcut.Control | Shortcut.Shift };
}
