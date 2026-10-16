using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using Jint;
using Jint.Native;
using Jint.Native.Object;
using Jint.Runtime;
using Jint.Runtime.Interop;
using Keyloom.Models;

namespace Keyloom.Engine;

/// <summary>Where a script went wrong, counted from line 1 of the script as the person wrote it.</summary>
public sealed record ScriptProblem(int Line, int Column, string Message)
{
    public override string ToString() => Line > 0 ? $"Line {Line}: {Message}" : Message;
}

/// <summary>One function a script can call, for the help page and the editor's suggestions.</summary>
public sealed record ScriptFunction(string Group, string Name, string Call, string About, string Example);

/// <summary>A mistake in how the script called one of Keyloom's functions. Scripts can catch it with try and catch.</summary>
public sealed class ScriptUsageException(string message) : ArgumentException(message);

/// <summary>Thrown by stop(): ends the macro on purpose, with an optional message.</summary>
public sealed class ScriptStoppedException(string message) : Exception(message);

/// <summary>
/// Runs a script macro: plain JavaScript with a small set of functions named after what they do, like press("E"),
/// clickOn("Play") and wait(200). Scripts reach the keyboard, the mouse and the screen through Keyloom only: there is no
/// file, network or system access.
/// </summary>
public sealed partial class MacroScript
{
    public const int MaxLength = 100_000;
    private const string Source = "script", PreludeSource = "keyloom";
    /// <summary>The person's code goes in a function, so return ends a run early and each run starts fresh.</summary>
    private const string Opening = "function __run() {", Closing = "\n}";
    private readonly Jint.Engine _engine;
    private readonly ExecutionInput _input;
    private readonly PointerResolver _pointer;
    private readonly TargetLocator _targets;
    private readonly GridLocator _grids;
    private readonly IWindowSystem _windows;
    private readonly IScreen _screen;
    private readonly IntPtr _window;
    private readonly Action<string> _log;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, ushort[]> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();

    internal MacroScript(string code, ExecutionInput input, PointerResolver pointer, TargetLocator targets, GridLocator grids,
        IWindowSystem windows, IScreen screen, IntPtr window, Action<string> log, CancellationToken token)
    {
        _input = input; _pointer = pointer; _targets = targets; _grids = grids; _windows = windows; _screen = screen; _window = window; _log = log; _token = token;
        _engine = new Jint.Engine(o =>
        {
            o.CancellationToken(token);
            o.LimitMemory(256_000_000);
            o.LimitRecursion(500);
            o.CatchClrExceptions(ex => ex is ScriptUsageException);
        });
        Define();
        _engine.Execute(Prelude, PreludeSource);
        _engine.SetValue("memory", new JsObject(_engine));
        try { _engine.Execute(Opening + code + Closing, Source); }
        catch (JavaScriptException ex) { throw new InvalidOperationException(Problem(ex).ToString()); }
    }

    /// <summary>Plays the script once. <paramref name="run"/> counts from 1 and is the script's <c>run</c>.</summary>
    /// <returns>False when the script called stop() without a message, which ends the macro quietly.</returns>
    public bool Run(long run)
    {
        _engine.SetValue("run", run);
        try { _engine.Invoke("__run"); return true; }
        catch (ExecutionCanceledException) { throw new OperationCanceledException(_token); }
        catch (JavaScriptException ex) when (ex.InnerException is OperationCanceledException) { throw new OperationCanceledException(_token); }
        catch (JavaScriptException ex) when (ex.InnerException is ScriptStoppedException stop)
        {
            if (stop.Message.Length == 0) return false;
            throw new InvalidOperationException(stop.Message);
        }
        catch (ScriptStoppedException stop)
        {
            if (stop.Message.Length == 0) return false;
            throw new InvalidOperationException(stop.Message);
        }
        catch (JavaScriptException ex) { throw new InvalidOperationException("The script stopped. " + Problem(ex)); }
        catch (MemoryLimitExceededException) { throw new InvalidOperationException("The script used too much memory and was stopped."); }
        catch (RecursionDepthOverflowException) { throw new InvalidOperationException("The script called itself too many times and was stopped."); }
        catch (ScriptUsageException ex) { throw new InvalidOperationException("The script stopped. " + ex.Message); }
    }

    /// <summary>The first mistake that keeps the script from running at all, or null when it reads fine.</summary>
    public static ScriptProblem? Check(string code)
    {
        if (code.Length > MaxLength) return new(0, 0, $"A script can be up to {MaxLength:N0} characters.");
        try { Jint.Engine.PrepareScript(Opening + code + Closing, Source); return null; }
        catch (ScriptPreparationException ex) when (ex.InnerException is Acornima.ParseErrorException parse)
        {
            int line = parse.LineNumber, column = line == 1 ? parse.Column - Opening.Length : parse.Column;
            // An unclosed brace is found at the closing line Keyloom adds; it belongs to the end of the script.
            int lines = code.Split('\n').Length;
            if (line > lines) { line = lines; column = 0; }
            return new(line, Math.Max(0, column), Plain(parse.Description));
        }
    }
    private static ScriptProblem Problem(JavaScriptException ex)
    {
        // The line in the person's script, even when the mistake surfaced inside one of Keyloom's own helpers.
        var at = ScriptLine().Match(ex.JavaScriptStackTrace ?? "");
        int line = at.Success ? int.Parse(at.Groups[1].Value, CultureInfo.InvariantCulture)
            : ex.Location.SourceFile == Source ? ex.Location.Start.Line : 0;
        return new(line, 0, Plain(ex.Message));
    }
    [GeneratedRegex(@"\bscript:(\d+):")] private static partial Regex ScriptLine();
    /// <summary>JavaScript's own messages, said a little more plainly where it helps.</summary>
    private static string Plain(string message)
    {
        message = Regex.Replace(message, @"\s*\(script:\d+:\d+\)$", "");
        if (message.EndsWith(" is not defined", StringComparison.Ordinal))
            return $"'{message[..^15]}' is not something Keyloom knows. Check its spelling, or open Help for the list of functions.";
        if (message.StartsWith("Unexpected end of input", StringComparison.Ordinal) || message.StartsWith("Unexpected token '}'", StringComparison.Ordinal) && message.Contains("end"))
            return "The script ends too early. A ( { or [ is not closed.";
        // Messages that start with a function name, like press(), keep its spelling.
        if (Regex.IsMatch(message, @"^\w+\(\)")) return message;
        return message.Length > 0 ? char.ToUpperInvariant(message[0]) + message[1..] : "Something went wrong.";
    }

    private void Define()
    {
        Function("press", a => { _input.Press(Keys(a, 0, "press")); return JsValue.Undefined; });
        Function("hold", a => { foreach (var k in Keys(a, 0, "hold")) _input.KeyDown(k); return JsValue.Undefined; });
        Function("release", a => { foreach (var k in Keys(a, 0, "release").Reverse()) _input.KeyUp(k); return JsValue.Undefined; });
        Function("type", a => { _input.Text(Text(a, 0, "type", "the text to type, such as type(\"Hello\")"), _token); return JsValue.Undefined; });
        Function("click", a =>
        {
            int next = MoveIfPoint(a, "click");
            _input.Click(Button(a, next, "click"));
            return JsValue.Undefined;
        });
        Function("mouseDown", a => { _input.MouseDown(Button(a, MoveIfPoint(a, "mouseDown"), "mouseDown")); return JsValue.Undefined; });
        Function("mouseUp", a => { _input.MouseUp(Button(a, MoveIfPoint(a, "mouseUp"), "mouseUp")); return JsValue.Undefined; });
        Function("moveTo", a =>
        {
            var (x, y, next) = Point(a, 0, "moveTo");
            int ms = a.Length > next && !a[next].IsUndefined() ? Whole(a, next, "moveTo", "the glide time in ms", 0, 60000) : 0;
            if (ms > 0) _input.Glide(x, y, ms, _token); else _input.Position(x, y);
            return JsValue.Undefined;
        });
        Function("moveBy", a => { _input.Move(Whole(a, 0, "moveBy", "how far right, such as moveBy(20, 0)"), Whole(a, 1, "moveBy", "how far down, such as moveBy(0, -10)")); return JsValue.Undefined; });
        Function("drag", a =>
        {
            var (fx, fy, next) = Point(a, 0, "drag");
            var (tx, ty, after) = Point(a, next, "drag");
            int ms = a.Length > after && !a[after].IsUndefined() ? Whole(a, after, "drag", "the drag time in ms", 1, 60000) : 150;
            string button = Button(a, after + 1, "drag");
            _input.Position(fx, fy);
            _input.MouseDown(button);
            Wait(MacroExecutor.DragPauseMs);
            _input.Glide(tx, ty, ms, _token);
            Wait(MacroExecutor.DragPauseMs);
            _input.MouseUp(button);
            return JsValue.Undefined;
        });
        Function("scroll", a => { _input.Scroll(Whole(a, 0, "scroll", "how many notches, such as scroll(-3) to scroll down")); return JsValue.Undefined; });
        Function("wait", a => { _input.Settle(); Wait(Whole(a, 0, "wait", "the time in ms, such as wait(500)", 0, 86_400_000)); return JsValue.Undefined; });
        Function("mouse", _ => { var (x, y) = _windows.Cursor(); return PointObject(x, y); });
        Function("inWindow", a =>
        {
            if (_window == IntPtr.Zero) throw new ScriptUsageException("inWindow() needs a target window. Pick one under More options.");
            var (x, y) = _pointer.Resolve(CoordinateSpace.Window, Whole(a, 0, "inWindow", "x inside the window"), Whole(a, 1, "inWindow", "y inside the window"));
            return PointObject(x, y);
        });
        Function("find", a => Found(_targets.Locate(Target(a, "find"))));
        Function("onScreen", a => _targets.Locate(Target(a, "onScreen")) != null);
        Function("clickOn", a =>
        {
            if (_targets.Locate(Target(a, "clickOn")) is not { } spot) return false;
            _input.Position(spot.Center.X, spot.Center.Y);
            _input.Click(Button(a, 1, "clickOn"));
            return true;
        });
        Function("waitFor", a => Watch(a, "waitFor", gone: false));
        Function("waitUntilGone", a => Watch(a, "waitUntilGone", gone: true));
        Function("colorAt", a =>
        {
            var (x, y, _) = Point(a, 0, "colorAt");
            var bounds = _screen.Bounds;
            if (x < bounds.X || y < bounds.Y || x >= bounds.X + bounds.Width || y >= bounds.Y + bounds.Height) return JsValue.Null;
            return TargetPicture.ColorText(_screen.Capture(x, y, 1, 1).Pixels[0]);
        });
        Function("__readGrid", a =>
        {
            string name = Text(a, 0, "readGrid", "the name of a grid, such as readGrid(\"Board\")");
            try { _grids.Get(name); } catch (ArgumentException) { throw new ScriptUsageException($"This macro has no grid called '{name}'. Mark one under Grids."); }
            var reading = _grids.Read(name);
            var o = new JsObject(_engine);
            o.Set("rows", reading.Rows); o.Set("columns", reading.Columns);
            o.Set("values", JsValue.FromObject(_engine, reading.Values.Select(v => v is { } n ? (object)(double)n : null).ToArray()));
            o.Set("xs", JsValue.FromObject(_engine, reading.Centers.Select(c => (double)c.X).ToArray()));
            o.Set("ys", JsValue.FromObject(_engine, reading.Centers.Select(c => (double)c.Y).ToArray()));
            var pair = reading.Pair();
            o.Set("pairFrom", pair?.From ?? -1); o.Set("pairTo", pair?.To ?? -1);
            return o;
        });
        Function("log", a =>
        {
            _log(string.Join(" ", a.Select(Show)));
            return JsValue.Undefined;
        });
        Function("stop", a => throw new ScriptStoppedException(a.Length > 0 && !a[0].IsUndefined() ? Show(a[0]) : ""));
        Function("random", a =>
        {
            int min = Whole(a, 0, "random", "the lowest number, such as random(1, 6)"), max = Whole(a, 1, "random", "the highest number, such as random(1, 6)");
            if (max < min) throw new ScriptUsageException("random() needs the lowest number first, such as random(1, 6).");
            return _random.Next(min, max + 1);
        });
    }
    private void Function(string name, Func<JsValue[], JsValue> body) =>
        _engine.SetValue(name, new ClrFunction(_engine, name, (_, args) => body(args)));
    private void Wait(int ms)
    {
        if (ms > 0 && _token.WaitHandle.WaitOne(ms)) _token.ThrowIfCancellationRequested();
        _token.ThrowIfCancellationRequested();
    }
    private JsValue Watch(JsValue[] a, string name, bool gone)
    {
        string target = Target(a, name);
        int timeout = a.Length > 1 && !a[1].IsUndefined() ? Whole(a, 1, name, "how long to wait in ms", 0, 86_400_000) : -1;
        _input.Settle();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if ((_targets.Locate(target) == null) == gone) return true;
            if (timeout >= 0 && clock.ElapsedMilliseconds >= timeout) return false;
            Wait(timeout < 0 ? MacroExecutor.LookEveryMs : (int)Math.Clamp(timeout - clock.ElapsedMilliseconds, 1, MacroExecutor.LookEveryMs));
        }
    }
    private string Target(JsValue[] a, string name)
    {
        string target = Text(a, 0, name, $"the name of a target, such as {name}(\"Play\")");
        try { _targets.Get(target); } catch (ArgumentException) { throw new ScriptUsageException($"This macro has no target called '{target}'. Add it under Targets."); }
        return target;
    }
    private JsValue Found(Match? match)
    {
        if (match is not { } m) return JsValue.Null;
        var o = PointObject(m.Center.X, m.Center.Y);
        o.Set("width", m.Width); o.Set("height", m.Height);
        return o;
    }
    private JsObject PointObject(int x, int y)
    {
        var o = new JsObject(_engine);
        o.Set("x", x); o.Set("y", y);
        return o;
    }
    /// <summary>A click or press at a point moves there first: click(x, y), click(point) or just click().</summary>
    private int MoveIfPoint(JsValue[] a, string name)
    {
        if (a.Length == 0 || a[0].IsString() || a[0].IsUndefined()) return 0;
        var (x, y, next) = Point(a, 0, name);
        _input.Position(x, y);
        return next;
    }
    /// <summary>A point as two numbers, or as anything with x and y, like what find() gives back.</summary>
    private static (int X, int Y, int Next) Point(JsValue[] a, int at, string name)
    {
        if (a.Length > at && a[at].IsObject() && a[at] is ObjectInstance o)
        {
            var x = o.Get("x"); var y = o.Get("y");
            if (!x.IsNumber() || !y.IsNumber()) throw new ScriptUsageException($"{name}() was given something without an x and a y.");
            return ((int)Math.Round(x.AsNumber()), (int)Math.Round(y.AsNumber()), at + 1);
        }
        if (a.Length > at && a[at].IsNull()) throw new ScriptUsageException($"{name}() was given nothing to go to. Was the target not found?");
        return (Whole(a, at, name, $"x and y, such as {name}(400, 300)"), Whole(a, at + 1, name, $"x and y, such as {name}(400, 300)"), at + 2);
    }
    private static int Whole(JsValue[] a, int at, string name, string what, int min = -1_000_000, int max = 1_000_000)
    {
        if (a.Length <= at || !a[at].IsNumber() || double.IsNaN(a[at].AsNumber())) throw new ScriptUsageException($"{name}() needs {what}.");
        double value = Math.Round(a[at].AsNumber());
        if (value < min || value > max) throw new ScriptUsageException($"{name}() takes {min:N0} to {max:N0} for {what.Split(',')[0]}, not {value:N0}.");
        return (int)value;
    }
    private static string Text(JsValue[] a, int at, string name, string what)
    {
        if (a.Length <= at || !a[at].IsString()) throw new ScriptUsageException($"{name}() needs {what}.");
        return a[at].AsString();
    }
    private ushort[] Keys(JsValue[] a, int at, string name)
    {
        string text = Text(a, at, name, $"a key, such as {name}(\"E\") or {name}(\"Ctrl+S\")");
        if (_keys.TryGetValue(text, out var keys)) return keys;
        try { return _keys[text] = KeyResolver.Resolve(text); }
        catch (ArgumentException ex) { throw new ScriptUsageException($"{name}(): {ex.Message}"); }
    }
    private static string Button(JsValue[] a, int at, string name)
    {
        if (a.Length <= at || a[at].IsUndefined()) return "Left";
        if (!a[at].IsString()) throw new ScriptUsageException($"{name}() takes a mouse button as text: \"left\", \"right\", \"middle\", \"back\" or \"forward\".");
        return a[at].AsString().ToLowerInvariant() switch
        {
            "left" => "Left", "right" => "Right", "middle" => "Middle", "back" or "x1" => "X1", "forward" or "x2" => "X2",
            var other => throw new ScriptUsageException($"{name}(): unknown mouse button \"{other}\". Use \"left\", \"right\", \"middle\", \"back\" or \"forward\".")
        };
    }
    private static string Show(JsValue v) => v switch
    {
        _ when v.IsString() => v.AsString(),
        _ when v.IsUndefined() => "undefined",
        _ when v.IsNull() => "null",
        _ when v.IsObject() && v is not Jint.Native.Function.Function => Json(v),
        _ => v.ToString()
    };
    private static string Json(JsValue v)
    {
        try { return new Jint.Native.Json.JsonSerializer(((ObjectInstance)v).Engine).Serialize(v).ToString(); }
        catch (JavaScriptException) { return v.ToString(); }
    }

    /// <summary>Grids as friendly objects. Rows and columns count from 1, as they read on the screen.</summary>
    private const string Prelude = """
        function readGrid(name) {
          const g = __readGrid(name);
          const cell = i => i < 0 ? null : { row: Math.floor(i / g.columns) + 1, column: i % g.columns + 1, value: g.values[i], x: g.xs[i], y: g.ys[i] };
          const all = () => g.values.map((_, i) => cell(i));
          const best = better => { let pick = -1; g.values.forEach((v, i) => { if (v !== null && (pick < 0 || better(v, g.values[pick]))) pick = i; }); return cell(pick); };
          return {
            name: name, rows: g.rows, columns: g.columns,
            at(row, column) {
              if (!(row >= 1 && row <= g.rows && column >= 1 && column <= g.columns))
                throw new Error(name + " has rows 1 to " + g.rows + " and columns 1 to " + g.columns + ", so at(" + row + ", " + column + ") is outside it.");
              return cell((row - 1) * g.columns + (column - 1));
            },
            cells: all,
            highest: () => best((a, b) => a > b),
            lowest: () => best((a, b) => a < b),
            find: value => cell(g.values.indexOf(value)),
            empty: () => all().filter(c => c.value === null),
            pair: () => g.pairFrom < 0 ? null : { from: cell(g.pairFrom), to: cell(g.pairTo), value: g.values[g.pairFrom] },
            toString() { let rows = []; for (let r = 0; r < g.rows; r++) rows.push(g.values.slice(r * g.columns, (r + 1) * g.columns).map(v => v === null ? "." : v).join("  ")); return rows.join("\n"); }
          };
        }
        """;

    /// <summary>Every function a script can call, as the help page and the suggestions list them.</summary>
    public static readonly IReadOnlyList<ScriptFunction> Functions =
    [
        new("Keyboard", "press", "press(key)", "Presses and lets go of a key or a combination.", "press(\"E\")\npress(\"Ctrl+S\")"),
        new("Keyboard", "hold", "hold(key)", "Holds a key down until release().", "hold(\"Shift\")"),
        new("Keyboard", "release", "release(key)", "Lets go of a key held with hold().", "release(\"Shift\")"),
        new("Keyboard", "type", "type(text)", "Types text into the app in front.", "type(\"gg\")"),
        new("Mouse", "click", "click(button?) · click(x, y, button?) · click(point, button?)", "Clicks where the pointer is, or moves to a point first. The button is \"left\" unless you say \"right\", \"middle\", \"back\" or \"forward\".", "click()\nclick(\"right\")\nclick(640, 360)"),
        new("Mouse", "mouseDown", "mouseDown(button?) · mouseDown(x, y, button?)", "Holds a mouse button down until mouseUp().", "mouseDown(\"left\")"),
        new("Mouse", "mouseUp", "mouseUp(button?) · mouseUp(x, y, button?)", "Lets go of a mouse button.", "mouseUp(\"left\")"),
        new("Mouse", "moveTo", "moveTo(x, y, ms?) · moveTo(point, ms?)", "Moves the pointer to a point on the screen. With a time, it glides there.", "moveTo(640, 360)\nmoveTo(640, 360, 200)"),
        new("Mouse", "moveBy", "moveBy(x, y)", "Moves the pointer from where it is. Negative goes left or up.", "moveBy(0, -40)"),
        new("Mouse", "drag", "drag(from, to, ms?, button?)", "Holds the button at one point, glides to the other and lets go. Points can be cells, found targets or { x, y }.", "drag({ x: 100, y: 100 }, { x: 300, y: 100 })"),
        new("Mouse", "scroll", "scroll(notches)", "Scrolls the wheel. Positive scrolls up, negative down.", "scroll(-3)"),
        new("Mouse", "mouse", "mouse()", "Where the pointer is now, as { x, y }.", "const at = mouse()\nlog(at.x, at.y)"),
        new("Mouse", "inWindow", "inWindow(x, y)", "Turns a point inside the target window into a point on the screen, so it follows the window.", "click(inWindow(200, 150))"),
        new("Waiting", "wait", "wait(ms)", "Pauses the script. 1000 ms is one second.", "wait(500)"),
        new("Waiting", "waitFor", "waitFor(target, ms?)", "Waits until a target shows on the screen. Gives true, or false if the time ran out. Without a time it waits as long as it takes.", "if (!waitFor(\"Ready\", 5000)) stop(\"Not ready\")"),
        new("Waiting", "waitUntilGone", "waitUntilGone(target, ms?)", "Waits until a target is no longer on the screen.", "waitUntilGone(\"Loading\")"),
        new("Screen", "onScreen", "onScreen(target)", "True when the target is on the screen right now.", "if (onScreen(\"Chest\")) clickOn(\"Chest\")"),
        new("Screen", "find", "find(target)", "Where the target is, as { x, y, width, height } with x and y in its middle, or null.", "const play = find(\"Play\")\nif (play) click(play)"),
        new("Screen", "clickOn", "clickOn(target, button?)", "Finds the target and clicks its middle. Gives true when it was there.", "clickOn(\"Play\")"),
        new("Screen", "colorAt", "colorAt(x, y) · colorAt(point)", "The color of one pixel, as \"#RRGGBB\".", "if (colorAt(20, 900) === \"#3FBF3F\") log(\"full health\")"),
        new("Grids", "readGrid", "readGrid(grid)", "Reads the numbers in a grid. Gives a grid with at(row, column), highest(), lowest(), find(number), empty(), pair() and cells(). Each cell has row, column, value, x and y, so click(cell) works.", "const board = readGrid(\"Board\")\nconst top = board.highest()\nif (top) click(top)"),
        new("Other", "log", "log(...things)", "Writes to the Output under the script, to see what the script sees.", "log(\"score\", 42)"),
        new("Other", "stop", "stop(message?)", "Ends the macro. With a message, Keyloom shows it.", "stop(\"Out of energy\")"),
        new("Other", "random", "random(min, max)", "A whole number from min to max, both included.", "wait(random(200, 400))"),
        new("Other", "memory", "memory", "An object that keeps what you put in it from one run to the next, until the macro stops.", "memory.count = (memory.count || 0) + 1"),
        new("Other", "run", "run", "Which run this is, counting from 1.", "if (run % 10 === 0) press(\"R\")")
    ];

    /// <summary>Ready-made scripts for the Examples menu: a name and the code.</summary>
    public static readonly IReadOnlyList<(string Name, string Code)> Examples =
    [
        ("Press keys in a rhythm", """
            // Presses 1, 2 and 3 with a short pause after each.
            for (const key of ["1", "2", "3"]) {
              press(key)
              wait(250)
            }
            """),
        ("Click a target when it shows", """
            // Waits up to 10 seconds for the target, then clicks it.
            // Add a target named "Play" under Targets first.
            if (waitFor("Play", 10000)) {
              clickOn("Play")
            } else {
              log("Play did not show up")
            }
            """),
        ("Do something every few runs", """
            // Set Runs to 0 so this repeats until you stop it.
            click()
            if (run % 20 === 0) {
              press("R")   // every 20th run
            }
            wait(random(80, 120))
            """),
        ("Merge the lowest pair in a grid", """
            // Mark a grid named "Board" under Grids first.
            const board = readGrid("Board")
            const pair = board.pair()
            if (pair) {
              log("merging", pair.value)
              drag(pair.from, pair.to)
            } else {
              stop("No two cells match")
            }
            """),
        ("Count and stop", """
            memory.clicks = (memory.clicks || 0) + 1
            click()
            if (memory.clicks >= 100) stop("Clicked 100 times")
            wait(50)
            """)
    ];
}
