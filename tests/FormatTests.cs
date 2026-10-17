using System.IO;
using System.Text.Json;
using Keyloom.Models;
using Keyloom.Services;

internal static partial class Program
{
    private static void FormatTests()
    {
        Check(Shortcut.Parse("Ctrl+Shift+F9") == (Shortcut.Control | Shortcut.Shift, 0x78u) && Shortcut.Parse("alt + 1") == (Shortcut.Alt, 0x31u)
            && Shortcut.Parse("") == (0u, 0u) && Shortcut.ToText(Shortcut.Control | Shortcut.Shift, 0x78) == "Ctrl+Shift+F9", "shortcuts read and write as text");
        Check(Fails(() => Shortcut.Parse("Ctrl+Shift"), "no key") && Fails(() => Shortcut.Parse("Ctrl+Nope"), "Unknown key \"Nope\""), "a shortcut without a real key is refused");

        static int Ms(string t) => TimeText.TryParse(t, out int ms) ? ms : -1;
        Check(Ms("250") == 250 && Ms("250ms") == 250 && Ms("2s") == 2000 && Ms("1.5s") == 1500 && Ms("10m") == 600000
            && Ms("1h") == 3600000 && Ms("1h 30m") == 5400000 && Ms("2M") == 120000 && Ms("") == -1 && Ms("2x") == -1 && Ms("m") == -1, "time fields read ms, s, m and h");
        Check(TimeText.Format(120000) == "2m" && TimeText.Format(5400000) == "1h 30m" && TimeText.Format(1500) == "1500" && TimeText.Format(50) == "50", "whole times show short");

        var seq = Profile(MacroBehaviorType.Sequence);
        seq.PrimaryVk = 0x78; seq.PrimaryModifiers = Shortcut.Control | Shortcut.Shift; seq.RepeatCount = 0; seq.PointerSettleMs = 35;
        seq.Window = new WindowTarget { ProcessName = "chrome", Title = "Reports", ClientWidth = 1280, ClientHeight = 720 };
        seq.IconKind = MacroIconKind.Glyph; seq.IconGlyph = "R";
        seq.Actions = new()
        {
            new() { ActionType = MacroActionType.MousePosition, X = 412, Y = 260, Space = CoordinateSpace.Window, GlideMs = 120 },
            new() { ActionType = MacroActionType.MouseClick, MouseButton = "Right" },
            new() { ActionType = MacroActionType.GroupStart, Name = "Burst", Repeat = 3, GapMs = 40 },
            new() { ActionType = MacroActionType.KeyPress, KeyName = "Ctrl+C" },
            new() { ActionType = MacroActionType.GroupStart },
            new() { ActionType = MacroActionType.MouseDown, MouseButton = "X1" },
            new() { ActionType = MacroActionType.MouseUp, MouseButton = "X1" },
            new() { ActionType = MacroActionType.GroupEnd },
            new() { ActionType = MacroActionType.GroupEnd },
            new() { ActionType = MacroActionType.KeyDown, KeyName = "Shift" },
            new() { ActionType = MacroActionType.KeyUp, KeyName = "Shift" },
            new() { ActionType = MacroActionType.Delay, DelayMs = 200 },
            new() { ActionType = MacroActionType.MouseMove, X = -5, Y = 9 },
            new() { ActionType = MacroActionType.MousePosition, X = 3, Y = -4, Space = CoordinateSpace.Start },
            new() { ActionType = MacroActionType.MouseScroll, ScrollAmount = -3 },
            new() { ActionType = MacroActionType.Text, Text = "Zażółć \"quotes\"\nline" }
        };
        var repeat = Profile(); repeat.TargetType = TargetInputType.Mouse; repeat.UsePoint = true; repeat.PointX = 40; repeat.PointY = 50;
        repeat.Window = new WindowTarget { ProcessName = "game" }; repeat.ExecutionMode = ExecutionMode.QuickSwitch; repeat.RestoreCursor = false;
        var hold = Profile(MacroBehaviorType.PressAndHold);
        string json = MacroFile.Write(new[] { seq, repeat, hold });
        var back = MacroFile.Read(json);
        Check(MacroFile.Write(back) == json && back.Count == 3 && back[0].Id == seq.Id, "every macro type and step kind survives a save and load");
        Check(back[0].Actions.Count == seq.Actions.Count && back[0].Actions[5].MouseButton == "X1" && back[0].Actions[^1].Text == seq.Actions[^1].Text
            && back[0].Window!.Title == "Reports" && back[0].IconGlyph == "R" && back[0].PointerSettleMs == 35, "nested groups, windows, icons and text keep their values");
        Check(json.Contains("\"do\": \"group\"") && json.Contains("\"shortcut\": \"Ctrl+Shift+F9\"") && json.Contains("Zażółć") && !json.Contains("ActionType"),
            "the file reads as words: nested groups, a text shortcut and plain letters");

        var written = MacroFile.Read("""
            // Written by hand, the way an AI tool would
            {
              "keyloom": 1,
              "macros": [
                {
                  "name": "Report buttons",
                  "shortcut": "Ctrl + Shift + F9",
                  "type": "Sequence",
                  "steps": [
                    { "do": "moveTo", "x": 412, "y": 260 },
                    { "do": "Click" },
                    { "do": "wait", "ms": 200 },
                    { "do": "group", "repeat": 2, "steps": [ { "do": "press", "key": "Enter" }, ] },
                  ],
                },
                { "name": "Spam E", "type": "repeat", "press": { "key": "E" }, "everyMs": 25 }
              ]
            }
            """);
        Check(written.Count == 2 && written[0].Actions.Count == 6 && written[0].Actions[1].MouseButton == "Left" && written[0].StartDelayMs == 500
            && written[0].Id.Length > 0 && written[1].SpamIntervalMs == 25 && written[1].TargetKey == "E", "a hand-written file with comments and missing ids loads with defaults");

        string File(string steps) => "{ \"keyloom\": 1, \"macros\": [ { \"name\": \"M\", \"type\": \"sequence\", \"steps\": [ " + steps + " ] } ] }";
        Check(Fails(() => MacroFile.Read(File("{ \"do\": \"wait\", \"ms\": 5 }, { \"do\": \"clik\" }")), "Macro \"M\": Step 2: unknown \"do\": \"clik\""), "an unknown step names the macro and step");
        Check(Fails(() => MacroFile.Read(File("{ \"do\": \"group\", \"steps\": [ { \"do\": \"wait\" } ] }")), "Step 1.1: it needs \"ms\""), "a step inside a group is named by its path");
        Check(Fails(() => MacroFile.Read(File("{ \"do\": \"wait\", \"ms\": 0 }")), "Step 1: step delay must be"), "value limits name the step");
        Check(Fails(() => MacroFile.Read(File("{ \"do\": \"wait\", \"msec\": 5 }")), "Unknown field \"msec\""), "a misspelled field is named");
        Check(Fails(() => MacroFile.Read("{ \"keyloom\": 2, \"macros\": [] }"), "newer Keyloom") && Fails(() => MacroFile.Read("{ \"macros\": [] }"), "\"keyloom\": 1"),
            "files from a newer Keyloom or without a format number are refused");
        Check(Fails(() => MacroFile.Read("{ \"keyloom\": 1, \"macros\": [ { \"name\": \"R\", \"type\": \"repeat\" } ] }"), "Add \"press\""), "a repeat without press says what to add");

        string folder = Path.Combine(Path.GetTempPath(), "keyloom-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        var storage = new ProfileStorageService(folder);
        var old = Profile(MacroBehaviorType.Sequence); old.Name = "From 0.2";
        old.Actions.Add(new() { ActionType = MacroActionType.MouseClick });
        System.IO.File.WriteAllText(storage.LegacyPath, JsonSerializer.Serialize(new[] { old }));
        var migrated = storage.LoadProfiles();
        Check(migrated.Profiles.Single().Name == "From 0.2" && migrated.Warning == "" && System.IO.File.Exists(storage.FilePath) && System.IO.File.Exists(storage.LegacyPath)
            && new ProfileStorageService(folder).LoadProfiles().Profiles.Single().Id == old.Id, "macros from 0.2 move to macros.keyloom and the old file stays");
        System.IO.File.Delete(storage.FilePath);
        System.IO.File.WriteAllText(storage.LegacyPath, "{broken");
        var unreadable = storage.LoadProfiles();
        Check(unreadable.Warning.Length > 0 && !System.IO.File.Exists(storage.FilePath), "an unreadable old file is not replaced by the default macro");
        Directory.Delete(folder, true);
    }

    private static bool Fails(Action action, string message)
    {
        try { action(); return false; }
        catch (Exception ex) when (ex is ArgumentException or JsonException)
        {
            if (!ex.Message.Contains(message, StringComparison.Ordinal)) throw new Exception($"Expected \"{message}\" in: {ex.Message}");
            return true;
        }
    }
}
