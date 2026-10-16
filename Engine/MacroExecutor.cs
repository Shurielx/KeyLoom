using System.Diagnostics;
using Keyloom.Models;

namespace Keyloom.Engine;

public sealed class MacroExecutor(IInputDriver? driver = null, IWindowSystem? windows = null, Func<IntPtr, IInputDriver>? backgroundDriver = null, Func<IntPtr, IInputDriver>? switchDriver = null, IScreen? screen = null, IGridReader? gridReader = null, Func<IAiVision?>? ai = null) : IAsyncDisposable
{
    private readonly IInputDriver _driver = driver ?? new InputSimulator();
    private readonly IWindowSystem _windows = windows ?? WindowSystem.Instance;
    private readonly Func<IntPtr, IInputDriver> _backgroundDriver = backgroundDriver ?? (window => new WindowMessageDriver(window, windows));
    private readonly Func<IntPtr, IInputDriver> _switchDriver = switchDriver ?? (window => new FocusSwitchDriver(driver ?? new InputSimulator(), window, windows));
    private readonly IScreen _screen = screen ?? ScreenCapture.Instance;
    private readonly IGridReader _gridReader = gridReader ?? WindowsOcrReader.Instance;
    /// <summary>The AI, made when a macro first asks it; null while AI is off in Settings.</summary>
    private readonly Func<IAiVision?> _ai = ai ?? (() => Services.AppSettings.Current.AiSetup is { } setup ? new AiVision(setup) : null);
    /// <summary>How often a step that looks for a target checks the screen again.</summary>
    public const int LookEveryMs = 50;
    /// <summary>A drag holds the button this long before it moves and after it arrives.</summary>
    public const int DragPauseMs = 40;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _task;
    private MacroProfile? _activeProfile;
    public MacroProfile? ActiveProfile => Volatile.Read(ref _activeProfile);
    public event Action<MacroProfile, bool>? ExecutionStateChanged;
    public event Action<MacroProfile, long>? ProgressChanged;
    public event Action<string>? ExecutionFailed;
    /// <summary>What a script wrote with log().</summary>
    public event Action<string>? ScriptLogged;

    public async Task ToggleAsync(MacroProfile profile)
    {
        await _gate.WaitAsync();
        try
        {
            bool stopOnly = ActiveProfile?.Id == profile.Id;
            await StopCoreAsync();
            if (stopOnly) return;
            ProfileValidator.Validate(profile);
            var snapshot = profile.Snapshot();
            ProfileValidator.Validate(snapshot);
            var keys = snapshot.TargetType == TargetInputType.Keyboard ? KeyResolver.Resolve(snapshot.TargetKey) : [];
            // Keys are resolved once per step object; group playback repeats the same objects.
            var stepKeys = new Dictionary<MacroAction, ushort[]>(ReferenceEqualityComparer.Instance);
            foreach (var a in snapshot.Actions.Where(a => a.ActionType is MacroActionType.KeyPress or MacroActionType.KeyDown or MacroActionType.KeyUp))
                stepKeys[a] = KeyResolver.Resolve(a.KeyName);
            IntPtr window = snapshot.NeedsWindow ? _windows.Find(snapshot.Window!) : IntPtr.Zero;
            if (snapshot.NeedsWindow && window == IntPtr.Zero)
                throw new ArgumentException(L.T($"Target window '{snapshot.Window!.Label}' was not found. Open it or pick the window again."));
            _cts = new();
            var token = _cts.Token;
            Volatile.Write(ref _activeProfile, profile);
            ExecutionStateChanged?.Invoke(profile, true);
            _task = Task.Run(() => Run(snapshot, profile, keys, stepKeys, window, token));
        }
        finally { _gate.Release(); }
    }
    public async Task StopAsync()
    {
        await _gate.WaitAsync();
        try { await StopCoreAsync(); }
        finally { _gate.Release(); }
    }
    private async Task StopCoreAsync()
    {
        _cts?.Cancel();
        if (_task != null) await _task;
        _cts?.Dispose(); _cts = null; _task = null;
    }
    private void Run(MacroProfile p, MacroProfile original, ushort[] keys, Dictionary<MacroAction, ushort[]> stepKeys, IntPtr window, CancellationToken token)
    {
        ExecutionInput? input = null;
        long count = 0;
        var progressClock = Stopwatch.StartNew();
        try
        {
            input = new ExecutionInput(_windows.Cursor, p.ExecutionMode switch
            {
                ExecutionMode.Background => _backgroundDriver(window),
                ExecutionMode.QuickSwitch => _switchDriver(window) is var quick && quick is FocusSwitchDriver focus ? WithCursor(focus, p.RestoreCursor) : quick,
                _ => _driver
            }, p.PointerSettleMs);
            Wait(p.StartDelayMs, token);
            var pointer = new PointerResolver(_windows, window, p);
            var targets = new TargetLocator(_screen, _windows, window, p.Targets);
            var grids = new GridLocator(_screen, _windows, window, p.Grids, _gridReader);
            var eyes = new AiEyes(_ai, _screen, _windows, window);
            var script = p.Behavior == MacroBehaviorType.Script
                ? new MacroScript(p.Script, input, pointer, targets, grids, eyes, _windows, _screen, window, line => ScriptLogged?.Invoke(line), token) : null;
            using var timer = new TimerResolution(p.Behavior == MacroBehaviorType.Script || p.Behavior != MacroBehaviorType.PressAndHold &&
                (p.SpamIntervalMs <= 16 || p.Actions.Any(a => a.ActionType == MacroActionType.Delay && a.DelayMs <= 16 || a.GlideMs > 0 || a.ActionType == MacroActionType.GroupStart && a.GapMs is > 0 and <= 16)));
            if (p.Behavior == MacroBehaviorType.PressAndHold)
            {
                if (p.TargetType == TargetInputType.Mouse && p.UsePoint)
                {
                    var (x, y) = pointer.Resolve(CoordinateSpace.Window, p.PointX, p.PointY);
                    input.Position(x, y);
                }
                if (p.TargetType == TargetInputType.Mouse) input.MouseDown(p.TargetMouseButton);
                else foreach (var key in keys) input.KeyDown(key);
                count = 1; ProgressChanged?.Invoke(original, count);
                token.WaitHandle.WaitOne();
            }
            else
            {
                while (p.RepeatCount == 0 || count < p.RepeatCount)
                {
                    token.ThrowIfCancellationRequested();
                    pointer.EnsureAlive();
                    if (p.Behavior == MacroBehaviorType.ClickSpam)
                    {
                        if (p.TargetType == TargetInputType.Mouse && p.UsePoint)
                        {
                            var (x, y) = pointer.Resolve(CoordinateSpace.Window, p.PointX, p.PointY);
                            input.Position(x, y);
                        }
                        if (p.TargetType == TargetInputType.Mouse) input.Click(p.TargetMouseButton);
                        else input.Press(keys);
                    }
                    else if (script != null)
                    {
                        if (!script.Run(count + 1)) { count++; break; }
                    }
                    else foreach (var step in StepGroups.Expand(p.Actions, block => block.AsksAi ? eyes.Holds(block, token) : Holds(block, targets, token)))
                    {
                        token.ThrowIfCancellationRequested();
                        pointer.EnsureAlive();
                        Execute(step, stepKeys.GetValueOrDefault(step, []), input, pointer, targets, grids, token);
                    }
                    count++;
                    if (progressClock.ElapsedMilliseconds >= 100)
                    {
                        ProgressChanged?.Invoke(original, count); progressClock.Restart();
                    }
                    if (p.RepeatCount != 0 && count == p.RepeatCount) break;
                    input.Settle();
                    Wait(p.SpamIntervalMs, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { ExecutionFailed?.Invoke(ex.Message); }
        finally
        {
            try { input?.ReleaseOwned(); }
            catch (Exception ex) { ExecutionFailed?.Invoke(ex.Message); }
            Volatile.Write(ref _activeProfile, null);
            ProgressChanged?.Invoke(original, count);
            ExecutionStateChanged?.Invoke(original, false);
        }
    }
    private static void Execute(MacroAction a, ushort[] keys, ExecutionInput input, PointerResolver pointer, TargetLocator targets, GridLocator grids, CancellationToken token)
    {
        switch (a.ActionType)
        {
            case MacroActionType.MouseClick: input.Click(a.MouseButton); break;
            case MacroActionType.MouseDown: input.MouseDown(a.MouseButton); break;
            case MacroActionType.MouseUp: input.MouseUp(a.MouseButton); break;
            case MacroActionType.KeyPress: input.Press(keys); break;
            case MacroActionType.KeyDown: foreach (var key in keys) input.KeyDown(key); break;
            case MacroActionType.KeyUp: foreach (var key in keys.Reverse()) input.KeyUp(key); break;
            case MacroActionType.Delay: input.Settle(); Wait(a.DelayMs, token); break;
            case MacroActionType.MouseMove: input.Move(a.X, a.Y); break;
            case MacroActionType.MousePosition:
                var (x, y) = pointer.Resolve(a.Space, a.X, a.Y);
                if (a.GlideMs > 0) input.Glide(x, y, a.GlideMs, token);
                else input.Position(x, y);
                break;
            case MacroActionType.MouseScroll: input.Scroll(a.ScrollAmount); break;
            case MacroActionType.Text: input.Text(a.Text, token); break;
            case MacroActionType.ClickTarget:
                if (Look(a, targets, token, found => found != null) is { } spot)
                {
                    var (cx, cy) = spot.Center;
                    input.Position(cx, cy);
                    input.Click(a.MouseButton);
                }
                break;
            case MacroActionType.ClickCell:
            {
                var reading = grids.Read(a.Grid);
                int cell = reading.Pick(a.Pick, a.CellValue);
                if (cell < 0)
                {
                    if (a.StopIfMissing) throw new InvalidOperationException(L.T($"{a.GridName} has no cell with {a.PickText}, so the macro stopped."));
                    break;
                }
                input.Position(reading.Centers[cell].X, reading.Centers[cell].Y);
                input.Click(a.MouseButton);
                break;
            }
            case MacroActionType.DragPair:
            {
                var reading = grids.Read(a.Grid);
                if (reading.Pair() is not var (from, to))
                {
                    if (a.StopIfMissing) throw new InvalidOperationException(L.T($"{a.GridName} has no two cells with the same number, so the macro stopped."));
                    break;
                }
                // Press, travel and let go, with short pauses so the game sees a drag and not a click.
                input.Position(reading.Centers[from].X, reading.Centers[from].Y);
                input.MouseDown(a.MouseButton);
                Wait(DragPauseMs, token);
                input.Glide(reading.Centers[to].X, reading.Centers[to].Y, Math.Max(a.GlideMs, 1), token);
                Wait(DragPauseMs, token);
                input.MouseUp(a.MouseButton);
                break;
            }
            case MacroActionType.WaitUntil:
                input.Settle();
                Look(a, targets, token, found => a.Gone ? found == null : found != null);
                break;
        }
    }
    /// <summary>
    /// Checks the screen until the condition holds or the step's time is up. Out of time, the macro goes on,
    /// or stops when the step says so.
    /// </summary>
    private static Match? Look(MacroAction a, TargetLocator targets, CancellationToken token, Func<Match?, bool> done)
    {
        var clock = Stopwatch.StartNew();
        // Clicking with no time set looks once; waiting with no time set waits for as long as it takes.
        bool forever = a.ActionType == MacroActionType.WaitUntil && a.TimeoutMs == 0;
        while (true)
        {
            token.ThrowIfCancellationRequested();
            var found = targets.Locate(a.Target);
            if (done(found)) return found;
            if (!forever && clock.ElapsedMilliseconds >= a.TimeoutMs)
            {
                if (!a.StopIfMissing) return null;
                throw new InvalidOperationException(a.ActionType == MacroActionType.ClickTarget || !a.Gone
                    ? L.T($"'{a.TargetName}' was not found on the screen, so the macro stopped.")
                    : L.T($"'{a.TargetName}' did not go away, so the macro stopped."));
            }
            Wait((int)Math.Min(LookEveryMs, Math.Max(1, forever ? LookEveryMs : a.TimeoutMs - clock.ElapsedMilliseconds)), token);
        }
    }
    /// <summary>
    /// Answers an If: whether its target is on the screen (or, for "is not", gone). It looks for up to the If's time
    /// for the answer to become yes, so a target that is about to show still counts.
    /// </summary>
    private static bool Holds(MacroAction block, TargetLocator targets, CancellationToken token)
    {
        var clock = Stopwatch.StartNew();
        while (true)
        {
            token.ThrowIfCancellationRequested();
            bool found = targets.Locate(block.Target) != null;
            if (found != block.Gone) return true;
            if (clock.ElapsedMilliseconds >= block.TimeoutMs) return false;
            Wait((int)Math.Clamp(block.TimeoutMs - clock.ElapsedMilliseconds, 1, LookEveryMs), token);
        }
    }
    private static FocusSwitchDriver WithCursor(FocusSwitchDriver driver, bool restore)
    {
        driver.RestoreCursor = restore;
        return driver;
    }
    private static void Wait(int milliseconds, CancellationToken token)
    {
        if (milliseconds > 0 && token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
