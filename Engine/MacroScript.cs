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
    public override string ToString() => Line > 0 ? L.T($"Line {Line}: {Message}") : Message;
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
public sealed partial class MacroScript : IDisposable
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
    private readonly AiEyes _eyes;
    private readonly IWindowSystem _windows;
    private readonly IScreen _screen;
    private readonly IntPtr _window;
    private readonly Action<string> _log;
    private readonly Func<IKeyListener> _newListener;
    private readonly KeyChord? _own;
    private readonly CancellationToken _token;
    private readonly Dictionary<string, ushort[]> _keys = new(StringComparer.OrdinalIgnoreCase);
    private readonly Random _random = new();

    internal MacroScript(string code, ExecutionInput input, PointerResolver pointer, TargetLocator targets, GridLocator grids,
        AiEyes eyes, IWindowSystem windows, IScreen screen, IntPtr window, Action<string> log, Func<IKeyListener> keys, KeyChord? own, CancellationToken token)
    {
        _input = input; _pointer = pointer; _targets = targets; _grids = grids; _eyes = eyes; _windows = windows; _screen = screen; _window = window; _log = log; _newListener = keys; _own = own; _token = token;
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
        return Guard(() => _engine.Invoke("__run"));
    }
    /// <summary>Plays part of the script and turns how it ended into what Keyloom shows.</summary>
    /// <returns>False when the script called stop() without a message, which ends the macro quietly.</returns>
    private bool Guard(Action play)
    {
        try { play(); return true; }
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
        catch (JavaScriptException ex) { throw new InvalidOperationException(L.T($"The script stopped. {Problem(ex)}")); }
        catch (MemoryLimitExceededException) { throw new InvalidOperationException(L.T("The script used too much memory and was stopped.")); }
        catch (RecursionDepthOverflowException) { throw new InvalidOperationException(L.T("The script called itself too many times and was stopped.")); }
        catch (ScriptUsageException ex) { throw new InvalidOperationException(L.T($"The script stopped. {ex.Message}")); }
    }

    /// <summary>The first mistake that keeps the script from running at all, or null when it reads fine.</summary>
    public static ScriptProblem? Check(string code)
    {
        if (code.Length > MaxLength) return new(0, 0, L.T($"A script can be up to {MaxLength:N0} characters."));
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
            return L.T($"'{message[..^15]}' is not something Keyloom knows. Check its spelling, or open Help for the list of functions.");
        if (message.StartsWith("Unexpected end of input", StringComparison.Ordinal) || message.StartsWith("Unexpected token '}'", StringComparison.Ordinal) && message.Contains("end"))
            return L.T("The script ends too early. A ( { or [ is not closed.");
        // Messages that start with a function name, like press(), keep its spelling.
        if (Regex.IsMatch(message, @"^\w+\(\)")) return message;
        return message.Length > 0 ? char.ToUpperInvariant(message[0]) + message[1..] : L.T("Something went wrong.");
    }

    private void Define()
    {
        Function("press", a => { _input.Press(Keys(a, 0, "press")); return JsValue.Undefined; });
        Function("hold", a => { foreach (var k in Keys(a, 0, "hold")) _input.KeyDown(k); return JsValue.Undefined; });
        Function("release", a => { foreach (var k in Keys(a, 0, "release").Reverse()) _input.KeyUp(k); return JsValue.Undefined; });
        Function("type", a => { _input.Text(Text(a, 0, "type", L.T("the text to type, such as type(\"Hello\")")), _token); return JsValue.Undefined; });
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
            int ms = a.Length > next && !a[next].IsUndefined() ? Whole(a, next, "moveTo", L.T("the glide time in ms"), 0, 60000) : 0;
            if (ms > 0) _input.Glide(x, y, ms, _token); else _input.Position(x, y);
            return JsValue.Undefined;
        });
        Function("moveBy", a => { _input.Move(Whole(a, 0, "moveBy", L.T("how far right, such as moveBy(20, 0)")), Whole(a, 1, "moveBy", L.T("how far down, such as moveBy(0, -10)"))); return JsValue.Undefined; });
        Function("drag", a =>
        {
            var (fx, fy, next) = Point(a, 0, "drag");
            var (tx, ty, after) = Point(a, next, "drag");
            int ms = a.Length > after && !a[after].IsUndefined() ? Whole(a, after, "drag", L.T("the drag time in ms"), 1, 60000) : 150;
            string button = Button(a, after + 1, "drag");
            _input.Position(fx, fy);
            _input.MouseDown(button);
            Wait(MacroExecutor.DragPauseMs);
            _input.Glide(tx, ty, ms, _token);
            Wait(MacroExecutor.DragPauseMs);
            _input.MouseUp(button);
            return JsValue.Undefined;
        });
        Function("scroll", a => { _input.Scroll(Whole(a, 0, "scroll", L.T("how many notches, such as scroll(-3) to scroll down"))); return JsValue.Undefined; });
        Function("wait", a => { _input.Settle(); Listen(Whole(a, 0, "wait", L.T("the time in ms, such as wait(500)"), 0, 86_400_000)); return JsValue.Undefined; });
        Function("mouse", _ => { var (x, y) = _windows.Cursor(); return PointObject(x, y); });
        Function("inWindow", a =>
        {
            if (_window == IntPtr.Zero) throw new ScriptUsageException(L.T("inWindow() needs a target window. Pick one under More options."));
            var (x, y) = _pointer.Resolve(CoordinateSpace.Window, Whole(a, 0, "inWindow", L.T("x inside the window")), Whole(a, 1, "inWindow", L.T("y inside the window")));
            return PointObject(x, y);
        });
        Function("fromStart", a =>
        {
            var (x, y) = _pointer.Resolve(CoordinateSpace.Start, Whole(a, 0, "fromStart", L.T("how far right, such as fromStart(20, 0)")), Whole(a, 1, "fromStart", L.T("how far down, such as fromStart(0, -10)")));
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
            string name = Text(a, 0, "readGrid", L.T("the name of a grid, such as readGrid(\"Board\")"));
            try { _grids.Get(name); } catch (ArgumentException) { throw new ScriptUsageException(L.T($"This macro has no grid called '{name}'. Mark one under Grids.")); }
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
        Function("ask", a => Ask(a, "ask", ""));
        Function("askYesNo", a => AiVision.YesNo(Ask(a, "askYesNo", "\nAnswer only yes or no.")) == true);
        Function("askNumber", a => AiVision.NumberIn(Ask(a, "askNumber", "\nAnswer only with the number.")) is { } n ? n : JsValue.Null);
        Function("log", a =>
        {
            _log(string.Join(" ", a.Select(Show)));
            return JsValue.Undefined;
        });
        Function("stop", a => throw new ScriptStoppedException(a.Length > 0 && !a[0].IsUndefined() ? Show(a[0]) : ""));
        DefineKeys();
        Function("random", a =>
        {
            int min = Whole(a, 0, "random", L.T("the lowest number, such as random(1, 6)")), max = Whole(a, 1, "random", L.T("the highest number, such as random(1, 6)"));
            if (max < min) throw new ScriptUsageException(L.T("random() needs the lowest number first, such as random(1, 6)."));
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
    /// <summary>
    /// Asks the AI about the screen. The second argument says where it looks: nothing for the target window or the whole
    /// screen, a target or grid name, an area { x, y, width, height }, or false for no picture at all.
    /// </summary>
    private string Ask(JsValue[] a, string name, string ending)
    {
        string question = Text(a, 0, name, L.T($"a question, such as {name}(\"How many coins are there?\")"));
        (int X, int Y, int Width, int Height)? area = null;
        bool look = true;
        if (a.Length > 1 && !a[1].IsUndefined() && !a[1].IsNull())
        {
            var where = a[1];
            if (where.IsBoolean()) look = where.AsBoolean();
            else if (where.IsString())
            {
                string thing = where.AsString();
                if (_grids.Has(thing)) area = _grids.Area(thing);
                else
                {
                    try { area = _targets.SearchArea(_targets.Get(thing)); }
                    catch (ArgumentException) { throw new ScriptUsageException(L.T($"{name}(): this macro has no target or grid called '{thing}'.")); }
                }
            }
            else if (where is ObjectInstance o && o.Get("width").IsNumber() && o.Get("height").IsNumber())
            {
                var (x, y, _) = Point([where], 0, name);
                area = (x, y, (int)o.Get("width").AsNumber(), (int)o.Get("height").AsNumber());
            }
            else throw new ScriptUsageException(L.T($"{name}() looks at a target or grid by name, an area {{ x, y, width, height }}, or nothing with false."));
        }
        _input.Settle();
        return _eyes.Ask(question + ending, area, look, _token);
    }
    private JsValue Watch(JsValue[] a, string name, bool gone)
    {
        string target = Target(a, name);
        int timeout = a.Length > 1 && !a[1].IsUndefined() ? Whole(a, 1, name, L.T("how long to wait in ms"), 0, 86_400_000) : -1;
        _input.Settle();
        var clock = Stopwatch.StartNew();
        while (true)
        {
            if ((_targets.Locate(target) == null) == gone) return true;
            if (timeout >= 0 && clock.ElapsedMilliseconds >= timeout) return false;
            Listen(timeout < 0 ? MacroExecutor.LookEveryMs : (int)Math.Clamp(timeout - clock.ElapsedMilliseconds, 1, MacroExecutor.LookEveryMs));
        }
    }
    private string Target(JsValue[] a, string name)
    {
        string target = Text(a, 0, name, L.T($"the name of a target, such as {name}(\"Play\")"));
        try { _targets.Get(target); } catch (ArgumentException) { throw new ScriptUsageException(L.T($"This macro has no target called '{target}'. Add it under Targets.")); }
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
            if (!x.IsNumber() || !y.IsNumber()) throw new ScriptUsageException(L.T($"{name}() was given something without an x and a y."));
            return ((int)Math.Round(x.AsNumber()), (int)Math.Round(y.AsNumber()), at + 1);
        }
        if (a.Length > at && a[at].IsNull()) throw new ScriptUsageException(L.T($"{name}() was given nothing to go to. Was the target not found?"));
        return (Whole(a, at, name, L.T($"x and y, such as {name}(400, 300)")), Whole(a, at + 1, name, L.T($"x and y, such as {name}(400, 300)")), at + 2);
    }
    private static int Whole(JsValue[] a, int at, string name, string what, int min = -1_000_000, int max = 1_000_000)
    {
        if (a.Length <= at || !a[at].IsNumber() || double.IsNaN(a[at].AsNumber())) throw new ScriptUsageException(L.T($"{name}() needs {what}."));
        double value = Math.Round(a[at].AsNumber());
        if (value < min || value > max) throw new ScriptUsageException(L.T($"{name}() takes {min:N0} to {max:N0} for {what.Split(',')[0]}, not {value:N0}."));
        return (int)value;
    }
    private static string Text(JsValue[] a, int at, string name, string what)
    {
        if (a.Length <= at || !a[at].IsString()) throw new ScriptUsageException(L.T($"{name}() needs {what}."));
        return a[at].AsString();
    }
    private ushort[] Keys(JsValue[] a, int at, string name)
    {
        string text = Text(a, at, name, L.T($"a key, such as {name}(\"E\") or {name}(\"Ctrl+S\")"));
        if (_keys.TryGetValue(text, out var keys)) return keys;
        try { return _keys[text] = KeyResolver.Resolve(text); }
        catch (ArgumentException ex) { throw new ScriptUsageException($"{name}(): {ex.Message}"); }
    }
    private static string Button(JsValue[] a, int at, string name)
    {
        if (a.Length <= at || a[at].IsUndefined()) return "Left";
        if (!a[at].IsString()) throw new ScriptUsageException(L.T($"{name}() takes a mouse button as text: \"left\", \"right\", \"middle\", \"back\" or \"forward\"."));
        return a[at].AsString().ToLowerInvariant() switch
        {
            "left" => "Left", "right" => "Right", "middle" => "Middle", "back" or "x1" => "X1", "forward" or "x2" => "X2",
            var other => throw new ScriptUsageException(L.T($"{name}(): unknown mouse button \"{other}\". Use \"left\", \"right\", \"middle\", \"back\" or \"forward\"."))
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
        new(L.T("Keyboard"), "press", "press(key)", L.T("Presses and lets go of a key or a combination."), "press(\"E\")\npress(\"Ctrl+S\")"),
        new(L.T("Keyboard"), "hold", "hold(key)", L.T("Holds a key down until release()."), "hold(\"Shift\")"),
        new(L.T("Keyboard"), "release", "release(key)", L.T("Lets go of a key held with hold()."), "release(\"Shift\")"),
        new(L.T("Keyboard"), "type", "type(text)", L.T("Types text into the app in front."), "type(\"gg\")"),
        new(L.T("Mouse"), "click", "click(button?) · click(x, y, button?) · click(point, button?)", L.T("Clicks where the pointer is, or moves to a point first. The button is \"left\" unless you say \"right\", \"middle\", \"back\" or \"forward\"."), "click()\nclick(\"right\")\nclick(640, 360)"),
        new(L.T("Mouse"), "mouseDown", "mouseDown(button?) · mouseDown(x, y, button?)", L.T("Holds a mouse button down until mouseUp()."), "mouseDown(\"left\")"),
        new(L.T("Mouse"), "mouseUp", "mouseUp(button?) · mouseUp(x, y, button?)", L.T("Lets go of a mouse button."), "mouseUp(\"left\")"),
        new(L.T("Mouse"), "moveTo", "moveTo(x, y, ms?) · moveTo(point, ms?)", L.T("Moves the pointer to a point on the screen. With a time, it glides there."), "moveTo(640, 360)\nmoveTo(640, 360, 200)"),
        new(L.T("Mouse"), "moveBy", "moveBy(x, y)", L.T("Moves the pointer from where it is. Negative goes left or up."), "moveBy(0, -40)"),
        new(L.T("Mouse"), "drag", "drag(from, to, ms?, button?)", L.T("Holds the button at one point, glides to the other and lets go. Points can be cells, found targets or { x, y }."), "drag({ x: 100, y: 100 }, { x: 300, y: 100 })"),
        new(L.T("Mouse"), "scroll", "scroll(notches)", L.T("Scrolls the wheel. Positive scrolls up, negative down."), "scroll(-3)"),
        new(L.T("Mouse"), "mouse", "mouse()", L.T("Where the pointer is now, as { x, y }."), "const at = mouse()\nlog(at.x, at.y)"),
        new(L.T("Mouse"), "inWindow", "inWindow(x, y)", L.T("Turns a point inside the target window into a point on the screen, so it follows the window."), "click(inWindow(200, 150))"),
        new(L.T("Mouse"), "fromStart", "fromStart(x, y)", L.T("A point measured from where the pointer was when the macro started. Negative goes left or up."), "moveTo(fromStart(40, -20))"),
        new(L.T("Waiting"), "wait", "wait(ms)", L.T("Pauses the script. 1000 ms is one second."), "wait(500)"),
        new(L.T("Waiting"), "waitFor", "waitFor(target, ms?)", L.T("Waits until a target shows on the screen. Gives true, or false if the time ran out. Without a time it waits as long as it takes."), "if (!waitFor(\"Ready\", 5000)) stop(\"Not ready\")"),
        new(L.T("Waiting"), "waitUntilGone", "waitUntilGone(target, ms?)", L.T("Waits until a target is no longer on the screen."), "waitUntilGone(\"Loading\")"),
        new(L.T("Screen"), "onScreen", "onScreen(target)", L.T("True when the target is on the screen right now."), "if (onScreen(\"Chest\")) clickOn(\"Chest\")"),
        new(L.T("Screen"), "find", "find(target)", L.T("Where the target is, as { x, y, width, height } with x and y in its middle, or null."), "const play = find(\"Play\")\nif (play) click(play)"),
        new(L.T("Screen"), "clickOn", "clickOn(target, button?)", L.T("Finds the target and clicks its middle. Gives true when it was there."), "clickOn(\"Play\")"),
        new(L.T("Screen"), "colorAt", "colorAt(x, y) · colorAt(point)", L.T("The color of one pixel, as \"#RRGGBB\"."), "if (colorAt(20, 900) === \"#3FBF3F\") log(\"full health\")"),
        new(L.T("Grids"), "readGrid", "readGrid(grid)", L.T("Reads the numbers in a grid. Gives a grid with at(row, column), highest(), lowest(), find(number), empty(), pair() and cells(). Each cell has row, column, value, x and y, so click(cell) works."), "const board = readGrid(\"Board\")\nconst top = board.highest()\nif (top) click(top)"),
        new(L.T("AI"), "ask", "ask(question, where?)", L.T("Asks the AI about the screen and gives back its answer as text. It looks at the target window, or the whole screen; give a target or grid name or { x, y, width, height } to show it less, or false to show it nothing. Needs AI on in Settings."), "const name = ask(\"What is the name of the open shop?\")\nlog(name)"),
        new(L.T("AI"), "askYesNo", "askYesNo(question, where?)", L.T("Asks a yes-or-no question about the screen. Gives true for yes and false for anything else."), "if (askYesNo(\"Is a boss on the screen?\")) press(\"Q\")"),
        new(L.T("AI"), "askNumber", "askNumber(question, where?)", L.T("Asks for a number on the screen, like coins or health. Gives the number, or null when there is none."), "const coins = askNumber(\"How many coins does the top bar show?\")\nif (coins !== null && coins >= 500) clickOn(\"Buy\")"),
        new(L.T("Other"), "log", "log(...things)", L.T("Writes to the Output under the script, to see what the script sees."), "log(\"score\", 42)"),
        new(L.T("Other"), "stop", "stop(message?)", L.T("Ends the macro. With a message, Keyloom shows it."), "stop(\"Out of energy\")"),
        new(L.T("Other"), "random", "random(min, max)", L.T("A whole number from min to max, both included."), "wait(random(200, 400))"),
        new(L.T("Other"), "memory", "memory", L.T("An object that keeps what you put in it from one run to the next, until the macro stops."), "memory.count = (memory.count || 0) + 1"),
        new(L.T("Other"), "run", "run", L.T("Which run this is, counting from 1."), "if (run % 10 === 0) press(\"R\")"),
        new(L.T("Settings, keys and buttons"), "on", "on(key, label?, function, { pass }?)", L.T("While the macro runs, pressing the key runs the function, at the script's next wait() or between runs. Keyloom keeps the key from the app in front unless you add { pass: true }. A script with keys keeps listening after its last run, until you stop it. The key and pass can be changed beside the script."), "on(\"1\", \"Start or pause\", () => memory.working = !memory.working)\non(\"Ctrl+2\", \"Read again\", () => log(readGrid(\"Board\").toString()), { pass: true })"),
        new(L.T("Settings, keys and buttons"), "button", "button(label, function)", L.T("Puts a button beside the script. Clicking it plays the script's first run as a test, then the function."), "button(\"Park the pointer\", () => moveTo(HOME))"),
        new(L.T("Settings, keys and buttons"), "setting", "const NAME = value // setting: Label", L.T("Shows a const beside the script as a field, so it can be changed without touching the code. A number gets a box, text in quotes a text box, true or false a checkbox, and { x, y } a point with Pick. Choices go after a comma, split by |. Text can ask for a picker after a comma: color, grid or target."), "const SPEED = 80 // setting: How fast\nconst MODE = \"merge\" // setting: Mode, merge | sell\nconst HOME = { x: 400, y: 300 } // setting: Where the pointer rests\nconst GLOW = \"#FFD200\" // setting: Glow, color\nconst BOARD = \"Board\" // setting: Board, grid")
    ];

    /// <summary>How to build a script that works like a small program, for the top of Help.</summary>
    public static readonly IReadOnlyList<(string Title, string Text)> Guide =
    [
        (L.T("Settings beside the script"), L.T("End a const with // setting: and a name, and it shows beside the script as a field. Numbers, text, true or false and choices get a box, a check or a list. A point { x, y } gets Pick, to click the spot on the screen. Add color, grid or target after a comma, as in // setting: Glow, color, for a color you pick on the screen or a list of this macro's grids or targets.")),
        (L.T("Keys while it runs"), L.T("on(\"1\", \"Start\", () => ...) runs the function when you press 1 while the macro runs. It runs at the script's next wait() or between runs, and a key's function never starts inside itself. Keyloom keeps the key from the game; { pass: true } lets the game get it too. Use a combination like \"Ctrl+1\" to keep plain keys free. The macro's own shortcut and Ctrl+Shift+F12 cannot be used.")),
        (L.T("Buttons in the editor"), L.T("button(\"Park\", () => ...) puts a button beside the script. Clicking it is a test run: the script's first run plays, then the button's function. Each key has a play button too.")),
        (L.T("Waiting for your keys"), L.T("A script with keys keeps listening after its last run, until you stop it. Keep what keys change in memory, set Runs to 0, and let the top of the script wait and return while there is nothing to do. The example below works that way."))
    ];
    /// <summary>The guide's example: a script that waits for keys and does its work when told to.</summary>
    public const string ProgramExample = """
        // Set Runs to 0. Press 1 to start or pause, 2 to park the pointer.
        const SPEED = 300 // setting: Pause after each merge (ms)
        const BOARD = "Board" // setting: Board, grid
        const HOME = { x: 400, y: 300 } // setting: Where the pointer rests

        on("1", "Start or pause", () => {
          memory.working = !memory.working
          log(memory.working ? "started" : "paused")
        })
        on("2", "Park the pointer", () => moveTo(HOME))
        button("Show the board", () => log(readGrid(BOARD).toString()))

        // Nothing to do until 1 is pressed.
        if (!memory.working) { wait(100); return }
        const pair = readGrid(BOARD).pair()
        if (pair) drag(pair.from, pair.to)
        wait(SPEED)
        """;

    /// <summary>Ready-made scripts for the Examples menu: a name and the code.</summary>
    public static readonly IReadOnlyList<(string Name, string Code)> Examples =
    [
        (L.T("Press keys in a rhythm"), """
            // Presses 1, 2 and 3 with a short pause after each.
            for (const key of ["1", "2", "3"]) {
              press(key)
              wait(250)
            }
            """),
        (L.T("Click a target when it shows"), """
            // Waits up to 10 seconds for the target, then clicks it.
            // Add a target named "Play" under Targets first.
            if (waitFor("Play", 10000)) {
              clickOn("Play")
            } else {
              log("Play did not show up")
            }
            """),
        (L.T("Do something every few runs"), """
            // Set Runs to 0 so this repeats until you stop it.
            click()
            if (run % 20 === 0) {
              press("R")   // every 20th run
            }
            wait(random(80, 120))
            """),
        (L.T("Merge the lowest pair in a grid"), """
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
        (L.T("A small program with keys and buttons"), ProgramExample),
        (L.T("Count and stop"), """
            memory.clicks = (memory.clicks || 0) + 1
            click()
            if (memory.clicks >= 100) stop("Clicked 100 times")
            wait(50)
            """)
    ];
}
