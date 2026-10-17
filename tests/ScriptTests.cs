using System.IO;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    private static MacroProfile ScriptMacro(string code, int runs = 1)
    {
        var p = Profile(MacroBehaviorType.Script);
        p.RepeatCount = runs; p.Script = code;
        return p;
    }

    private static async Task ScriptEngineTests()
    {
        Check(MacroScript.Check("press(\"E\")\nreturn\n") == null, "a script that reads fine has no problem, and return ends it early");
        Check(MacroScript.Check("press(\"E\"\nwait(100)") is { Line: 2 } && MacroScript.Check("const = 4") is { Line: 1, Column: >= 5 },
            "a mistake is reported on the line the person wrote it");
        Check(MacroScript.Check("if (true) {\n  press(\"E\")\n") is { Line: 3 } problem && problem.Message.Contains("ends too early"),
            "a brace left open is reported at the end of the script, in plain words");
        Check(Fails(() => ProfileValidator.Validate(ScriptMacro("")), "Write a script") && Fails(() => ProfileValidator.Validate(ScriptMacro("click(")), "Script line 1"),
            "an empty script or one that does not read cannot be saved");

        var screen = new FakeScreen(640, 400);
        var reader = new FakeGridReader(4, 2, null, 8, 2, 4);
        var driver = new FakeDriver();
        await using var executor = new MacroExecutor(driver, screen: screen, gridReader: reader);
        string failure = "";
        var logged = new List<string>();
        executor.ExecutionFailed += m => failure = m;
        executor.ScriptLogged += line => { lock (logged) logged.Add(line); };
        async Task Play(MacroProfile p)
        {
            failure = ""; driver.Events.Clear(); lock (logged) logged.Clear();
            await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        }

        await Play(ScriptMacro("press(\"E\")\nclick(\"right\")\nclick(100, 50)\nmoveBy(5, -5)\nscroll(-2)"));
        Check(driver.Events.SequenceEqual(["K69:down", "K69:up", "MRight:down", "MRight:up", "position:100,50", "MLeft:down", "MLeft:up", "move:5,-5", "scroll:-2"]) && failure == "",
            "a script presses keys, clicks, moves and scrolls in the order it is written");

        await Play(ScriptMacro("memory.n = (memory.n || 0) + 1\nlog(\"run\", run, \"seen\", memory.n, { a: 1 })", 3));
        Check(logged.SequenceEqual(["run 1 seen 1 {\"a\":1}", "run 2 seen 2 {\"a\":1}", "run 3 seen 3 {\"a\":1}"]), "each run knows its number, memory carries over, and log writes what it is given");

        var p = ScriptMacro("if (!onScreen(\"Play\")) log(\"not yet\")\nconst at = find(\"Play\")\nif (at) click(at)\nlog(clickOn(\"Play\", \"middle\"))");
        p.Targets.Add(new ScreenTarget { Name = "Play", Picture = TargetPicture.Encode(Button()) });
        await Play(p);
        Check(logged.SequenceEqual(["not yet", "false"]) && driver.Events.IsEmpty, "a target that is not on the screen is not clicked");
        screen.Paint(300, 200, Button());
        await Play(p);
        Check(driver.Events.SequenceEqual(["position:312,207", "MLeft:down", "MLeft:up", "position:312,207", "MMiddle:down", "MMiddle:up"]) && logged.SequenceEqual(["true"]),
            "find gives the middle of the target, and clickOn clicks it there");
        p.Script = "log(waitFor(\"Play\", 100), waitUntilGone(\"Play\", 60), colorAt(0, 0))";
        await Play(p);
        Check(logged.SingleOrDefault() == "true false #141414", "waitFor and waitUntilGone give true or false, and colorAt reads one pixel");

        p = ScriptMacro("const board = readGrid(\"Board\")\nlog(board.highest().value, board.at(2, 3).value, board.empty().length, board.find(8).column)\nconst pair = board.pair()\ndrag(pair.from, pair.to, 30)");
        p.Grids.Add(new ScreenGrid { Name = "Board", Area = new(100, 50, 300, 200), Rows = 2, Columns = 3 });
        await Play(p);
        var events = driver.Events.ToArray();
        Check(logged.SingleOrDefault() == "8 4 1 1" && events[0] == "position:250,200" && events[1] == "MLeft:down" && events[^2] == "position:250,100" && events[^1] == "MLeft:up",
            "a script reads a grid by rows and columns from 1 and drags its lowest pair");

        await Play(ScriptMacro("click()\nstop()\nclick()", 5));
        Check(driver.Events.Count == 2 && failure == "", "stop() without a message ends the macro quietly");
        await Play(ScriptMacro("stop(\"Out of energy\")"));
        Check(failure == "Out of energy", "stop() with a message shows it");
        await Play(ScriptMacro("\n\npres(\"E\")"));
        Check(failure.Contains("Line 3") && failure.Contains("'pres' is not something Keyloom knows"), "a misspelled function says its line and what to check");
        await Play(ScriptMacro("press()"));
        Check(failure.Contains("Line 1") && failure.Contains("press() needs a key, such as press(\"E\")"), "a function called the wrong way says how to call it");
        await Play(ScriptMacro("try { clickOn(\"Nope\") } catch (e) { log(\"caught\", e.message) }"));
        Check(logged.SingleOrDefault()?.StartsWith("caught This macro has no target called 'Nope'") == true && failure == "", "a script can catch Keyloom's errors itself");
        await Play(ScriptMacro("const g = readGrid(\"Board\")"));
        Check(failure.Contains("no grid called 'Board'"), "a grid the macro lacks is named in the error");

        failure = "";
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await executor.ToggleAsync(ScriptMacro("while (true) {}"));
        await Task.Delay(100);
        await executor.StopAsync();
        Check(executor.ActiveProfile == null && clock.ElapsedMilliseconds < 2000 && failure == "", "a script that never ends still stops when you stop it");
        await executor.ToggleAsync(ScriptMacro("while (true) wait(1000)"));
        await Task.Delay(50);
        clock.Restart();
        await executor.StopAsync();
        Check(clock.ElapsedMilliseconds < 500, "stopping does not wait for a script's wait to finish");

        // Scripts in the macro file, one line per entry so they read well.
        var file = ScriptMacro("// Click and wait\nclick()\nwait(100)", 0);
        string json = MacroFile.Write([file]);
        var back = MacroFile.Read(json).Single();
        Check(json.Contains("\"type\": \"script\"") && json.Contains("\"// Click and wait\",") && !json.Contains("\"press\"") && back.Script == file.Script && back.Behavior == MacroBehaviorType.Script,
            "a script is written one line per entry and reads back the same");
        Check(Fails(() => MacroFile.Read(json.Replace("\"wait(100)\"", "\"wait(100\"")), "Script line 3"), "a file with a script that does not read is refused with its line");
        string doc = File.ReadAllText(Path.Combine(RepoRoot(), "docs", "SCRIPTS.md"));
        Check(MacroScript.Functions.All(f => doc.Contains($"`{f.Name}")), "every script function is in docs/SCRIPTS.md");
    }
    private static string RepoRoot([System.Runtime.CompilerServices.CallerFilePath] string file = "") => Path.GetDirectoryName(Path.GetDirectoryName(file))!;

    private static void ScriptUiTests()
    {
        bool scripts = AppSettings.Current.Scripts;
        try
        {
            AppSettings.Current.Scripts = false;
            var plain = new MacroEditorView(Profile(MacroBehaviorType.Sequence));
            Check(((System.Windows.Controls.RadioButton)plain.FindName("ScriptRadio")).Visibility != System.Windows.Visibility.Visible, "Script stays out of the editor while the experiment is off");

            var p = ScriptMacro("// Merge\nconst board = readGrid(\"Board\")\nif (onScreen(\"Play\")) clickOn(\"Play\")\nlog(board.toString())", 0);
            p.Targets.Add(new ScreenTarget { Name = "Play", Picture = TargetPicture.Encode(Button()) });
            p.Grids.Add(new ScreenGrid { Name = "Board", Area = new(100, 50, 300, 200), Rows = 2, Columns = 3 });
            var editor = new MacroEditorView(p);
            var host = new System.Windows.Window { Content = editor, Width = 1100, Height = 760, Left = -20000, Top = -20000, ShowActivated = false };
            host.Show(); Pump(250);
            T Get<T>(string name) => (T)editor.FindName(name);
            var box = Get<ICSharpCode.AvalonEdit.TextEditor>("ScriptBox");
            Check(Get<System.Windows.Controls.RadioButton>("ScriptRadio").IsVisible && Get<System.Windows.Controls.Border>("ScriptPanel").IsVisible
                && !Get<System.Windows.Controls.Border>("SequencePanel").IsVisible && !Get<System.Windows.Controls.Button>("PreviewButton").IsVisible
                && Get<System.Windows.Controls.Button>("TestRunButton").IsVisible && box.Text == p.Script,
                "a script macro opens in the script editor, even with the experiment off, with Test run and no Preview");
            Check(Get<System.Windows.Controls.TextBlock>("ScriptStatus").Text == "Reads fine · 4 lines", "the editor says the script reads fine and how long it is");
            Check(Get<System.Windows.Controls.StackPanel>("ScriptAssetList").Children.Count == 2, "the targets and grids the script can use are listed beside it");
            var grids = (List<ScreenGrid>)Field(editor, "_grids");
            Invoke(editor, "RenameAsset", "Board", "Field", new Action<string>(n => grids[0].Name = n));
            Check(box.Text.Contains("readGrid(\"Field\")") && !box.Text.Contains("Board"), "renaming a grid renames it where the script names it");
            box.Document.Insert(box.Document.Text.Length, "\nwait(");
            Invoke(editor, "CheckScript");
            Check(Get<System.Windows.Controls.TextBlock>("ScriptStatus").Text.StartsWith("Line 5:"), "a mistake shows its line under the script as soon as it is typed");
            box.Document.Text = p.Script.Replace("Board", "Field");
            Invoke(editor, "CheckScript");

            box.CaretOffset = box.Document.Text.Length;
            box.Document.Insert(box.CaretOffset, "\ncl");
            box.CaretOffset = box.Document.Text.Length;
            Invoke(editor, "Suggest", true);
            var completion = (ICSharpCode.AvalonEdit.CodeCompletion.CompletionWindow?)Field(editor, "_completion");
            Check(completion != null && completion.CompletionList.CompletionData.Any(d => d.Text == "clickOn") && completion.CompletionList.CompletionData.Any(d => d.Text == "click"),
                "typing suggests Keyloom's functions");
            completion!.Close(); Pump(50);
            box.Document.Text = p.Script.Replace("Board", "Field");

            editor.ShowScriptOutput("4  2  .");
            editor.ShowScriptOutput("The script stopped. Line 2: This macro has no grid called 'Nope'.", true);
            Check(Get<System.Windows.Controls.TextBlock>("ScriptOutput").Text == "4  2  .\nStopped: The script stopped. Line 2: This macro has no grid called 'Nope'." && box.TextArea.Caret.Line == 2,
                "Output shows what the script logged and why it stopped, and jumps to that line");
            Render(editor, 1100, 760, "script-editor.png");

            var read = (MacroProfile)editor.GetType().GetMethod("ReadForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(editor, [p.Snapshot()])!;
            Check(read.Behavior == MacroBehaviorType.Script && read.Script == box.Text && read.Actions.Count == 0 && read.Grids.Single().Name == "Field" && read.Targets.Count == 1,
                "saving keeps the script and every target and grid it can use");

            var help = new ScriptHelpView(["Play"], ["Field"]);
            var sheetHost = new System.Windows.Window { Content = help, Width = 720, Height = 760, Left = -20000, Top = -20000, ShowActivated = false };
            sheetHost.Show(); Pump(150);
            Render(help, 720, 760, "script-help.png");
            var search = Descendants<System.Windows.Controls.TextBox>(help).First();
            search.Text = "drag"; Pump(50);
            var buttons = Descendants<System.Windows.Controls.Button>(help).Where(b => b.Content as string == "Insert").ToList();
            Check(buttons.Count == 1, "searching help narrows the list to what matches");
            buttons[0].RaiseEvent(new System.Windows.RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
            Check(help.Chosen?.StartsWith("drag(") == true, "Insert hands the example to the script");
            sheetHost.Close();

            AppSettings.Current.Scripts = true;
            var fresh = new MacroEditorView(Profile(MacroBehaviorType.ClickSpam));
            host.Content = fresh; Pump(150);
            ((System.Windows.Controls.RadioButton)fresh.FindName("ScriptRadio")).IsChecked = true; Pump(50);
            Check(((ICSharpCode.AvalonEdit.TextEditor)fresh.FindName("ScriptBox")).Text == MacroEditorView.StarterScript && ((System.Windows.Controls.StackPanel)fresh.FindName("ScriptSide")).IsVisible,
                "with the experiment on, a macro can turn into a script, which starts from a short example");
            host.Close();
        }
        finally { AppSettings.Current.Scripts = scripts; }
    }

    /// <summary>Plays a sequence and the script made from it on the same fake screen and compares every input they send.</summary>
    private static async Task SequenceScriptTests()
    {
        var screen = new FakeScreen(640, 400);
        screen.Paint(300, 200, Button());
        var reader = new FakeGridReader(4, 2, null, 8, 2, 4);
        var windows = new FakeWindows { Origin = (50, 30), CursorAt = (10, 20) };
        var vision = new FakeVision("yes");
        var driver = new FakeDriver();
        await using var executor = new MacroExecutor(driver, windows, screen: screen, gridReader: reader, ai: () => vision);
        string failure = "";
        executor.ExecutionFailed += m => failure = m;
        async Task<(string[] Events, string Failure, long Ms)> Play(MacroProfile p)
        {
            failure = ""; driver.Events.Clear();
            var clock = System.Diagnostics.Stopwatch.StartNew();
            await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
            // A glide's steps depend on timing; only where each run of moves ends is compared.
            var events = driver.Events.ToArray();
            return (events.Where((e, i) => !(e.StartsWith("position:") && i + 1 < events.Length && events[i + 1].StartsWith("position:"))).ToArray(), failure, clock.ElapsedMilliseconds);
        }
        MacroProfile Steps(MacroAction[] steps)
        {
            var p = Profile(MacroBehaviorType.Sequence);
            p.RepeatCount = 2; p.StartDelayMs = 0; p.PointerSettleMs = 0;
            p.Window = new WindowTarget { ProcessName = "game", ClientWidth = 800, ClientHeight = 600 };
            p.Targets.Add(new ScreenTarget { Name = "Play", Picture = TargetPicture.Encode(Button()) });
            p.Targets.Add(new ScreenTarget { Name = "Gone", Kind = ScreenTargetKind.Color, Color = "#FF00FF" });
            p.Grids.Add(new ScreenGrid { Name = "Board", Area = new(100, 50, 300, 200), Rows = 2, Columns = 3 });
            p.Grids.Add(new ScreenGrid { Name = "Moving", Area = new(100, 50, 300, 200), Rows = 2, Columns = 3, Follow = "Play", FollowX = 302, FollowY = 197 });
            foreach (var s in steps) p.Actions.Add(s);
            return p;
        }
        static MacroAction Do(MacroActionType kind, Action<MacroAction>? set = null) { var a = new MacroAction { ActionType = kind }; set?.Invoke(a); return a; }
        static MacroAction Open(BlockKind block, Action<MacroAction>? set = null) => Do(MacroActionType.GroupStart, a => { a.Block = block; set?.Invoke(a); });
        static MacroAction End() => Do(MacroActionType.GroupEnd);
        async Task<string> Same(string what, params MacroAction[] steps)
        {
            var seq = Steps(steps);
            ProfileValidator.Validate(seq);
            var script = seq.Snapshot();
            script.Behavior = MacroBehaviorType.Script; script.Script = SequenceScript.From(seq.Actions); script.Actions = new();
            ProfileValidator.Validate(script);
            var a = await Play(seq);
            var b = await Play(script);
            Check(a.Events.Length > 0 && a.Events.SequenceEqual(b.Events) && a.Failure == b.Failure,
                $"steps turned into a script send the same input: {what}" + (a.Events.SequenceEqual(b.Events) ? "" : $"\n  steps:  {string.Join(" ", a.Events)}\n  script: {string.Join(" ", b.Events)}\n{script.Script}"));
            return script.Script;
        }

        await Same("press", Do(MacroActionType.KeyPress, a => a.KeyName = "E"), Do(MacroActionType.KeyPress, a => a.KeyName = "Ctrl+S"));
        await Same("hold and release", Do(MacroActionType.KeyDown, a => a.KeyName = "Shift"), Do(MacroActionType.KeyPress, a => a.KeyName = "A"), Do(MacroActionType.KeyUp, a => a.KeyName = "Shift"));
        string typed = await Same("type", Do(MacroActionType.Text, a => a.Text = "Hi \"you\"\n\\ok\tą"));
        Check(typed.Contains("type(\"Hi \\\"you\\\"\\n\\\\ok\\tą\")"), "typed text keeps its quotes, line breaks and backslashes in the script");
        await Same("click and buttons", Do(MacroActionType.MouseClick), Do(MacroActionType.MouseClick, a => a.MouseButton = "Right"),
            Do(MacroActionType.MouseDown, a => a.MouseButton = "Middle"), Do(MacroActionType.MouseUp, a => a.MouseButton = "Middle"), Do(MacroActionType.MouseClick, a => a.MouseButton = "X2"));
        string moves = await Same("move to the screen, the window and the start, and glide",
            Do(MacroActionType.MousePosition, a => { a.X = 100; a.Y = 50; }), Do(MacroActionType.MouseClick),
            Do(MacroActionType.MousePosition, a => { a.X = 20; a.Y = 10; a.Space = CoordinateSpace.Window; }), Do(MacroActionType.MouseClick),
            Do(MacroActionType.MousePosition, a => { a.X = 5; a.Y = -5; a.Space = CoordinateSpace.Start; }), Do(MacroActionType.MouseClick),
            Do(MacroActionType.MousePosition, a => { a.X = 200; a.Y = 100; a.GlideMs = 30; }), Do(MacroActionType.MouseClick));
        Check(moves.Contains("moveTo(inWindow(20, 10))") && moves.Contains("moveTo(fromStart(5, -5))") && moves.Contains("moveTo(200, 100, 30)"), "a move says what it is measured from, and how long it glides");
        await Same("move by, scroll and wait", Do(MacroActionType.MouseMove, a => { a.X = 5; a.Y = -5; }), Do(MacroActionType.MouseScroll, a => a.ScrollAmount = -3),
            Do(MacroActionType.Delay, a => a.DelayMs = 20), Do(MacroActionType.MouseScroll, a => a.ScrollAmount = 2));
        await Same("click on a target", Do(MacroActionType.ClickTarget, a => { a.Target = "Play"; a.MouseButton = "Right"; }),
            Do(MacroActionType.ClickTarget, a => a.Target = "Gone"), Do(MacroActionType.ClickTarget, a => { a.Target = "Play"; a.TimeoutMs = 60; }), Do(MacroActionType.KeyPress, a => a.KeyName = "E"));
        await Same("click on a target that is missing, which stops", Do(MacroActionType.KeyPress, a => a.KeyName = "E"), Do(MacroActionType.ClickTarget, a => { a.Target = "Gone"; a.TimeoutMs = 60; a.StopIfMissing = true; }), Do(MacroActionType.KeyPress, a => a.KeyName = "Q"));
        await Same("click on a missing target at once, which stops", Do(MacroActionType.KeyPress, a => a.KeyName = "E"), Do(MacroActionType.ClickTarget, a => { a.Target = "Gone"; a.StopIfMissing = true; }));
        await Same("wait until", Do(MacroActionType.WaitUntil, a => a.Target = "Play"), Do(MacroActionType.KeyPress, a => a.KeyName = "E"),
            Do(MacroActionType.WaitUntil, a => { a.Target = "Gone"; a.Gone = true; }), Do(MacroActionType.WaitUntil, a => { a.Target = "Gone"; a.TimeoutMs = 40; }), Do(MacroActionType.KeyPress, a => a.KeyName = "Q"));
        await Same("wait until with a time that stops", Do(MacroActionType.KeyPress, a => a.KeyName = "E"), Do(MacroActionType.WaitUntil, a => { a.Target = "Play"; a.Gone = true; a.TimeoutMs = 40; a.StopIfMissing = true; }));
        string ifs = await Same("if and else by a target",
            Open(BlockKind.If, a => a.Target = "Play"), Do(MacroActionType.KeyPress, a => a.KeyName = "1"), End(), Open(BlockKind.Else), Do(MacroActionType.KeyPress, a => a.KeyName = "2"), End(),
            Open(BlockKind.If, a => a.Target = "Gone"), Do(MacroActionType.KeyPress, a => a.KeyName = "3"), End(), Open(BlockKind.Else), Do(MacroActionType.KeyPress, a => a.KeyName = "4"), End(),
            Open(BlockKind.If, a => { a.Target = "Play"; a.Gone = true; a.TimeoutMs = 40; }), Do(MacroActionType.KeyPress, a => a.KeyName = "5"), End(),
            Open(BlockKind.If, a => { a.Target = "Gone"; a.Gone = true; }), Do(MacroActionType.KeyPress, a => a.KeyName = "6"), End());
        Check(ifs.Contains("} else {") && MacroScript.Check(ifs) == null, "an If and its Else become if and else");
        await Same("if by asking the AI", Open(BlockKind.If, a => a.Question = "Is a boss here?"), Do(MacroActionType.KeyPress, a => a.KeyName = "1"), End(), Open(BlockKind.Else), Do(MacroActionType.KeyPress, a => a.KeyName = "2"), End(),
            Open(BlockKind.If, a => { a.Question = "Is the shop open?"; a.Gone = true; }), Do(MacroActionType.KeyPress, a => a.KeyName = "3"), End());
        var groupSteps = new[]
        {
            Open(BlockKind.Group, a => { a.Name = "Farm"; a.Repeat = 3; a.GapMs = 15; }),
                Open(BlockKind.If, a => a.Target = "Gone"), Do(MacroActionType.KeyPress, a => a.KeyName = "1"), End(),
                Do(MacroActionType.KeyPress, a => a.KeyName = "2"),
                Open(BlockKind.Group, a => a.Repeat = 2), Do(MacroActionType.MouseClick), Do(MacroActionType.MouseScroll, a => a.ScrollAmount = 1), End(),
                Open(BlockKind.If, a => a.Target = "Play"), Do(MacroActionType.KeyPress, a => a.KeyName = "3"), End(), Open(BlockKind.Else), End(),
            End(),
            Open(BlockKind.Group, a => a.GapMs = 10), Do(MacroActionType.KeyPress, a => a.KeyName = "4"), Do(MacroActionType.KeyPress, a => a.KeyName = "5"), End()
        };
        string groups = await Same("groups with repeats and a wait between steps", groupSteps);
        Check(groups.Contains("// Farm") && groups.Contains("for (let i = 0; i < 3; i++) {") && groups.Contains("for (let j = 0; j < 2; j++) {") && groups.Contains("if (i > 0) wait(15)")
            && groups.Contains("if (played) wait(15)") && groups.Contains("played = true"), "groups become loops that wait between runs and between steps");
        // A group's waits add no input, so the time it takes shows them: 3 runs of 4 steps and 2 waits between runs, twice.
        var timedSteps = Steps(groupSteps); timedSteps.RepeatCount = 1;
        var timedScript = timedSteps.Snapshot(); timedScript.Behavior = MacroBehaviorType.Script; timedScript.Script = groups; timedScript.Actions = new();
        long seqMs = (await Play(timedSteps)).Ms, scriptMs = (await Play(timedScript)).Ms;
        Check(Math.Abs(seqMs - scriptMs) < 60 && scriptMs >= 110, $"a script made from groups waits as long as the steps do ({seqMs} and {scriptMs} ms)");
        await Same("click a cell", Do(MacroActionType.ClickCell, a => { a.Grid = "Board"; a.Pick = CellPick.Highest; }), Do(MacroActionType.ClickCell, a => { a.Grid = "Board"; a.Pick = CellPick.Lowest; a.MouseButton = "Right"; }),
            Do(MacroActionType.ClickCell, a => { a.Grid = "Board"; a.Pick = CellPick.Equals; a.CellValue = 8; }), Do(MacroActionType.ClickCell, a => { a.Grid = "Board"; a.Pick = CellPick.Empty; }),
            Do(MacroActionType.ClickCell, a => { a.Grid = "Board"; a.Pick = CellPick.Equals; a.CellValue = 77; }), Do(MacroActionType.ClickCell, a => { a.Grid = "Moving"; a.Pick = CellPick.Highest; }));
        await Same("click a cell that is missing, which stops", Do(MacroActionType.KeyPress, a => a.KeyName = "E"), Do(MacroActionType.ClickCell, a => { a.Grid = "Board"; a.Pick = CellPick.Equals; a.CellValue = 77; a.StopIfMissing = true; }));
        await Same("drag a matching pair, on a grid that moves with a picture", Do(MacroActionType.DragPair, a => { a.Grid = "Board"; a.GlideMs = 20; }), Do(MacroActionType.DragPair, a => { a.Grid = "Moving"; a.MouseButton = "Right"; }));
        reader.Values = [1, 2, 3, null, 5, 6];
        await Same("drag a pair that is not there, which stops", Do(MacroActionType.KeyPress, a => a.KeyName = "E"), Do(MacroActionType.DragPair, a => { a.Grid = "Board"; a.StopIfMissing = true; }));
        Check(SequenceScript.From([]).Trim().StartsWith("//") && MacroScript.Check(SequenceScript.From([])) == null, "no steps make a script with only a note");
    }

    private static void ScriptSettingsTests()
    {
        string code = "const SPEED = 80 // setting: How fast\nlet NAME = 'Bo\\'b'; // Setting: Who\nconst LOUD = true //setting:\nvar MODE = \"fast\" // setting: Mode, fast | slow\nconst X = 1 // not a setting\nconst MORE = 5 // settings, not a mark";
        var settings = ScriptSettings.Read(code);
        Check(settings.Select(s => (s.Name, s.Label, s.Kind)).SequenceEqual([("SPEED", "How fast", ScriptSettingKind.Number), ("NAME", "Who", ScriptSettingKind.Text), ("LOUD", "LOUD", ScriptSettingKind.Switch), ("MODE", "Mode", ScriptSettingKind.Choice)])
            && settings[1].Value == "Bo'b" && settings[3].Choices.SequenceEqual(["fast", "slow"]) && settings[0].Line == 1,
            "a const marked // setting: is a setting: numbers, text, true or false, and choices after a comma");
        string written = ScriptSettings.Write(code, settings[0], "120.5");
        written = ScriptSettings.Write(written, ScriptSettings.Read(written)[1], "Al \"x\" 'y'");
        Check(written.StartsWith("const SPEED = 120.5 // setting: How fast\nlet NAME = 'Al \"x\" \\'y\\''; // Setting: Who\n") && ScriptSettings.Read(written)[1].Value == "Al \"x\" 'y'",
            "writing a setting changes only its value, in its own quotes");
        Check(Fails(() => ScriptSettings.Write(code, settings[0], "fast"), "must be a number") && Fails(() => ScriptSettings.Write(code, settings[2], "yes"), "on or off"),
            "a value that does not suit the setting is refused");
        Check(MacroScript.Check(written) == null && MacroScript.Functions.Any(f => f.Name == "setting"), "a script with settings is still plain JavaScript, and Help explains settings");
    }

    private static void SequenceScriptUiTests()
    {
        bool scripts = AppSettings.Current.Scripts;
        try
        {
            AppSettings.Current.Scripts = true;
            var p = Profile(MacroBehaviorType.Sequence);
            p.StartDelayMs = 700; p.PointerSettleMs = 35; p.RepeatCount = 4;
            p.Targets.Add(new ScreenTarget { Name = "Play", Picture = TargetPicture.Encode(Button()) });
            p.Grids.Add(new ScreenGrid { Name = "Board", Area = new(100, 50, 300, 200), Rows = 2, Columns = 3, Follow = "Play", FollowX = 10, FollowY = 10 });
            p.Actions.Add(new MacroAction { ActionType = MacroActionType.ClickTarget, Target = "Play" });
            p.Actions.Add(new MacroAction { ActionType = MacroActionType.DragPair, Grid = "Board" });
            var editor = new MacroEditorView(p);
            var host = new System.Windows.Window { Content = editor, Width = 1100, Height = 760, Left = -20000, Top = -20000, ShowActivated = false };
            host.Show(); Pump(250);
            T Get<T>(string name) => (T)editor.FindName(name);
            var box = Get<ICSharpCode.AvalonEdit.TextEditor>("ScriptBox");
            Check(Get<System.Windows.Controls.Button>("ToScriptButton").IsVisible, "a sequence offers Turn into script while scripts are on");
            editor.TurnIntoScriptAsync(ask: false).GetAwaiter().GetResult(); Pump(100);
            Check(Get<System.Windows.Controls.RadioButton>("ScriptRadio").IsChecked == true && box.Text == SequenceScript.From(p.Actions) && box.Text.Contains("clickOn(\"Play\")")
                && Get<System.Windows.Controls.TextBlock>("ScriptStatus").Text.StartsWith("Reads fine"), "Turn into script opens the steps as a script that reads fine");
            Check(editor.UndoShortcut(System.Windows.Input.ModifierKeys.Control, System.Windows.Input.Key.Z) && Get<System.Windows.Controls.RadioButton>("SequenceRadio").IsChecked == true
                && ((System.Collections.ObjectModel.ObservableCollection<MacroAction>)Field(editor, "_steps")).Count == 2, "Ctrl+Z goes back to the steps");
            editor.TurnIntoScriptAsync(ask: false).GetAwaiter().GetResult(); Pump(50);
            var read = (MacroProfile)editor.GetType().GetMethod("ReadForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(editor, [p.Snapshot()])!;
            Check(read.Behavior == MacroBehaviorType.Script && read.Actions.Count == 0 && read.Script == box.Text && read.StartDelayMs == 700 && read.PointerSettleMs == 35 && read.RepeatCount == 4
                && read.Targets.Single().Name == "Play" && read.Grids.Single().Follow == "Play", "saving the script keeps the targets, grids, Follow, Runs, start delay and pointer settle");
            Check(p.Behavior == MacroBehaviorType.Sequence && p.Actions.Count == 2, "until it is saved, the macro keeps its steps, so Cancel gets them back");

            // Settings beside the script.
            box.Document.Text = "const SPEED = 80 // setting: How fast\nconst SAY = \"hi\" // setting: Greeting\nconst LOUD = false // setting: Loud\nconst MODE = \"fast\" // setting: Mode, fast | slow\nwait(SPEED)\n";
            Invoke(editor, "CheckScript");
            var list = Get<System.Windows.Controls.StackPanel>("ScriptSettingList");
            var fields = Descendants<System.Windows.Controls.Control>(list).Where(c => c is System.Windows.Controls.TextBox or System.Windows.Controls.CheckBox or System.Windows.Controls.ComboBox).ToList();
            var speed = (System.Windows.Controls.TextBox)fields[0];
            Check(list.Children.Count == 4 && speed.Text == "80" && Descendants<System.Windows.Controls.TextBlock>(list).Any(t => t.Text == "How fast")
                && fields[2] is System.Windows.Controls.CheckBox { IsChecked: false } && fields[3] is System.Windows.Controls.ComboBox { SelectedItem: "fast" }
                && !Get<System.Windows.Controls.TextBlock>("ScriptSettingsHint").IsVisible, "a script's settings show beside it as fields with their labels");
            Render(editor, 1100, 760, "script-settings.png");
            speed.Text = "120";
            ((System.Windows.Controls.TextBox)fields[1]).Text = "say \"yo\"";
            ((System.Windows.Controls.CheckBox)fields[2]).IsChecked = true;
            ((System.Windows.Controls.ComboBox)fields[3]).SelectedItem = "slow";
            Check(box.Text == "const SPEED = 120 // setting: How fast\nconst SAY = \"say \\\"yo\\\"\" // setting: Greeting\nconst LOUD = true // setting: Loud\nconst MODE = \"slow\" // setting: Mode, fast | slow\nwait(SPEED)\n",
                "changing a field rewrites that value in the script");
            speed.Text = "12x";
            Check(box.Text.StartsWith("const SPEED = 120 ") && Get<System.Windows.Controls.TextBlock>("ErrorText").Text.Contains("must be a number"), "a field with a wrong value leaves the script as it was and says why");
            box.Document.Text = box.Text.Replace("SPEED = 120", "SPEED = 45").Replace("LOUD = true", "LOUD = false");
            Invoke(editor, "CheckScript");
            Check(speed.Text == "45" && fields[2] is System.Windows.Controls.CheckBox { IsChecked: false } && list.Children.Count == 4, "typing in the script updates the fields");
            box.Document.Text = box.Text.Replace("// setting: Greeting", "");
            Invoke(editor, "CheckScript");
            Check(list.Children.Count == 3, "a setting whose mark is removed leaves the panel");
            host.Close();
        }
        finally { AppSettings.Current.Scripts = scripts; }
    }
}
