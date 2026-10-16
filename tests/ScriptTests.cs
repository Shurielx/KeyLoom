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
}
