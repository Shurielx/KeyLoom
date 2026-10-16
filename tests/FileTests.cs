using System.IO;
using System.Reflection;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    /// <summary>Import and export of .keyloom files, and the click sound.</summary>
    private static void FileUiTests()
    {
        var wave = ClickSound.Wave();
        Check(wave.Length > 44 && wave[0] == 'R' && wave[8] == 'W' && BitConverter.ToInt32(wave, 40) == wave.Length - 44
            && Enumerable.Range(0, (wave.Length - 44) / 2).Max(i => Math.Abs((int)BitConverter.ToInt16(wave, 44 + i * 2))) < short.MaxValue / 4,
            "the click sound is a short, quiet wave");
        string settingsPath = Path.Combine(Path.GetTempPath(), "keyloom-sound-" + Guid.NewGuid().ToString("N") + ".json");
        Check(AppSettings.Load(settingsPath).ClickSounds, "click sounds are on until muted");
        var settings = new SettingsView(settingsPath);
        ((System.Windows.Controls.CheckBox)settings.FindName("SoundBox")).IsChecked = false;
        Check(!AppSettings.Load(settingsPath).ClickSounds, "click sounds can be muted in Settings");
        File.Delete(settingsPath); AppSettings.Load(settingsPath);

        // Preparing: nothing already there is replaced.
        var mine = Profile(); mine.Name = "Mine"; mine.PrimaryVk = 0x78; mine.PrimaryModifiers = Shortcut.Control;
        var same = mine.Snapshot(); same.Name = "Same id and shortcut";
        var free = Profile(); free.Name = "Free"; free.PrimaryVk = 0x79; free.PrimaryModifiers = Shortcut.Control;
        var twin = Profile(); twin.Name = "Twin"; twin.PrimaryVk = 0x79; twin.PrimaryModifiers = Shortcut.Control;
        var pictured = Profile(); pictured.IconKind = MacroIconKind.Image; pictured.IconImage = "missing.png";
        var plan = MacroImport.Prepare([mine], [same, free, twin, pictured], _ => false);
        Check(plan.Macros.Count == 4 && same.Id != mine.Id && plan.Macros.Select(p => p.Id).Distinct().Count() == 4, "imported macros get a new id when theirs is taken");
        Check(same.PrimaryVk == 0 && free.PrimaryVk == 0x79 && twin.PrimaryVk == 0 && plan.ShortcutsCleared.SequenceEqual(["Same id and shortcut", "Twin"]),
            "a taken shortcut is unset on import, also between imported macros");
        Check(pictured.IconKind == MacroIconKind.Auto && pictured.IconImage == "", "a picture icon missing on this PC falls back to the automatic icon");
        Check(MacroImport.Describe(plan).Contains("4 macros") && MacroImport.Describe(plan).Contains("'Twin'"), "the import question says what comes in and which shortcuts are left out");

        // Through the window, with storage in a temporary folder.
        string folder = Path.Combine(Path.GetTempPath(), "keyloom-files-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var main = new MainWindow { Left = -20000, Top = -20000, ShowActivated = false };
        typeof(MainWindow).GetField("_storage", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(main, new ProfileStorageService(folder));
        main.Show(); Pump(200);
        var profiles = (System.Collections.ObjectModel.ObservableCollection<MacroProfile>)Field(main, "_profiles");
        profiles.Clear();
        var seq = Profile(MacroBehaviorType.Sequence); seq.Name = "Shared"; seq.PrimaryVk = 0x75; seq.PrimaryModifiers = Shortcut.Control | Shortcut.Shift;
        seq.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
        profiles.Add(seq);
        string exported = Path.Combine(folder, "out.keyloom");
        bool wrote = (bool)typeof(MainWindow).GetMethod("Export", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [exported, profiles.ToList()])!;
        Check(wrote && MacroFile.Read(File.ReadAllText(exported)).Single().Name == "Shared", "export writes the macros as a .keyloom file");
        Task Import(string path) => (Task)typeof(MainWindow).GetMethod("ImportAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(main, [path, false])!;
        void Wait(Task t) { while (!t.IsCompleted) Pump(); t.GetAwaiter().GetResult(); }
        Wait(Import(exported));
        var stored = new ProfileStorageService(folder).LoadProfiles().Profiles;
        Check(profiles.Count == 2 && stored.Count == 2 && profiles[1].Id != seq.Id && profiles[1].PrimaryVk == 0 && seq.PrimaryVk == 0x75,
            "importing a macro you already have adds a copy without the shortcut, and saves it");
        string broken = Path.Combine(folder, "broken.keyloom");
        File.WriteAllText(broken, "{ \"keyloom\": 1, \"macros\": [ { \"name\": \"X\", \"type\": \"sequence\", \"steps\": [ { \"do\": \"clik\" } ] } ] }");
        Wait(Import(broken));
        var status = (System.Windows.Controls.TextBlock)main.FindName("StatusText");
        Check(profiles.Count == 2 && status.Text.Contains("broken.keyloom") && status.Text.Contains("\"clik\""), "a broken file is not imported and says what is wrong");
        File.WriteAllText(broken, "{ \"keyloom\": 1, \"macros\": [] }");
        Wait(Import(broken));
        Check(profiles.Count == 2 && status.Text.Contains("no macros"), "a file without macros says so");
        main.Hide();
        Directory.Delete(folder, true);
    }
}
