using System.Diagnostics;
using Jint;
using Jint.Native;
using Jint.Native.Object;

namespace Keyloom.Engine;

/// <summary>
/// Keys a script reacts to while it runs, and buttons it shows in the editor. A key's function runs at the script's next
/// wait(), or between runs, and never inside itself: pressing its key again while it runs waits until it ends. A script
/// that listens for keys keeps listening after its last run, until it is stopped.
/// </summary>
public sealed partial class MacroScript
{
    /// <summary>Presses waiting for their function beyond this many are dropped.</summary>
    private const int MaxWaiting = 16;
    private sealed record Handler(JsValue Function, bool Pass);
    private readonly Dictionary<KeyChord, Handler> _handlers = [];
    private readonly Dictionary<string, JsValue> _buttons = new(StringComparer.Ordinal);
    private readonly HashSet<KeyChord> _busy = [];
    private readonly List<KeyChord> _waiting = [];
    private IKeyListener? _listener;
    /// <summary>True once the script listens for a key with on().</summary>
    public bool Listening => _handlers.Count > 0;

    private void DefineKeys()
    {
        Function("on", a =>
        {
            string text = Text(a, 0, "on", L.T("a key, such as on(\"1\", \"Start\", () => log(\"started\"))"));
            KeyChord chord;
            try { chord = KeyChord.Parse(text); }
            catch (ArgumentException ex) { throw new ScriptUsageException($"on(): {ex.Message}"); }
            if (chord == KeyChord.Stop) throw new ScriptUsageException(L.T("on(): Ctrl+Shift+F12 stops every macro, so a script cannot use it."));
            if (chord == _own) throw new ScriptUsageException(L.T($"on(): {text} starts and stops this macro. Pick another key."));
            int at = a.Length > 1 && a[1].IsString() ? 2 : 1;
            if (a.Length <= at || a[at] is not Jint.Native.Function.Function function)
                throw new ScriptUsageException(L.T("on() needs a function to run, such as on(\"1\", \"Start\", () => log(\"started\"))."));
            bool pass = false;
            if (a.Length > at + 1 && !a[at + 1].IsUndefined())
            {
                var given = a[at + 1] is ObjectInstance options ? options.Get("pass") : JsValue.Null;
                if (!given.IsBoolean() && !given.IsUndefined())
                    throw new ScriptUsageException(L.T("on() takes { pass: true } after the function, so the app in front gets the key too."));
                pass = given.IsBoolean() && given.AsBoolean();
            }
            _handlers[chord] = new(function, pass);
            _listener ??= _newListener();
            _listener.Listen(_handlers.ToDictionary(h => h.Key, h => h.Value.Pass));
            return JsValue.Undefined;
        });
        Function("button", a =>
        {
            string label = Text(a, 0, "button", L.T("a label, such as button(\"Reset\", () => memory.count = 0)"));
            if (a.Length <= 1 || a[1] is not Jint.Native.Function.Function function)
                throw new ScriptUsageException(L.T("button() needs a function to run, such as button(\"Reset\", () => memory.count = 0)."));
            _buttons[label] = function;
            return JsValue.Undefined;
        });
    }

    /// <summary>Waits, running the functions of keys pressed meanwhile. -1 waits until the macro is stopped.</summary>
    private void Listen(int ms)
    {
        if (_listener == null) { if (ms < 0) _token.WaitHandle.WaitOne(); Wait(Math.Max(ms, 0)); return; }
        var clock = Stopwatch.StartNew();
        while (true)
        {
            Dispatch();
            int left = ms < 0 ? -1 : (int)Math.Max(0, ms - clock.ElapsedMilliseconds);
            if (left == 0) { _token.ThrowIfCancellationRequested(); return; }
            int woke = WaitHandle.WaitAny([_token.WaitHandle, _listener.Pressed], left);
            _token.ThrowIfCancellationRequested();
            if (woke == WaitHandle.WaitTimeout) return;
        }
    }
    private void Dispatch()
    {
        while (true)
        {
            while (_listener!.TryTake(out var pressed)) if (_waiting.Count < MaxWaiting) _waiting.Add(pressed);
            int next = _waiting.FindIndex(c => !_busy.Contains(c));
            if (next < 0) return;
            var chord = _waiting[next];
            _waiting.RemoveAt(next);
            if (!_handlers.TryGetValue(chord, out var handler)) continue;
            _busy.Add(chord);
            try { _engine.Call(handler.Function); }
            finally { _busy.Remove(chord); }
        }
    }

    /// <summary>Between runs: waits, running the functions of keys pressed meanwhile. -1 waits until the macro is stopped.</summary>
    /// <returns>False when a key's function called stop() without a message.</returns>
    public bool Idle(int ms) => Guard(() => { _input.Settle(); Listen(ms); });

    /// <summary>Runs a button's function, or a key's as if it was pressed, for trying it from the editor.</summary>
    /// <returns>False when the function called stop() without a message.</returns>
    public bool Press(string name)
    {
        JsValue? function = _buttons.GetValueOrDefault(name);
        if (function == null)
        {
            try { function = _handlers.GetValueOrDefault(KeyChord.Parse(name))?.Function; }
            catch (ArgumentException) { }
        }
        if (function == null) throw new InvalidOperationException(L.T($"The script did not set up '{name}' on its first run, so there was nothing to try."));
        return Guard(() => _engine.Call(function));
    }

    public void Dispose() => _listener?.Dispose();
}
