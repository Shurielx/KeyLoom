using System.Diagnostics;
using Keyloom.Models;

namespace Keyloom.Engine;

public sealed class MacroExecutor(IInputDriver? driver = null, IWindowSystem? windows = null, Func<IntPtr, IInputDriver>? backgroundDriver = null, Func<IntPtr, IInputDriver>? switchDriver = null) : IAsyncDisposable
{
    private readonly IInputDriver _driver = driver ?? new InputSimulator();
    private readonly IWindowSystem _windows = windows ?? WindowSystem.Instance;
    private readonly Func<IntPtr, IInputDriver> _backgroundDriver = backgroundDriver ?? (window => new WindowMessageDriver(window, windows));
    private readonly Func<IntPtr, IInputDriver> _switchDriver = switchDriver ?? (window => new FocusSwitchDriver(driver ?? new InputSimulator(), window, windows));
    private readonly SemaphoreSlim _gate = new(1, 1);
    private CancellationTokenSource? _cts;
    private Task? _task;
    private MacroProfile? _activeProfile;
    public MacroProfile? ActiveProfile => Volatile.Read(ref _activeProfile);
    public event Action<MacroProfile, bool>? ExecutionStateChanged;
    public event Action<MacroProfile, long>? ProgressChanged;
    public event Action<string>? ExecutionFailed;

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
                throw new ArgumentException($"Target window '{snapshot.Window!.Label}' was not found. Open it or pick the window again.");
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
            input = new ExecutionInput(p.ExecutionMode switch
            {
                ExecutionMode.Background => _backgroundDriver(window),
                ExecutionMode.QuickSwitch => _switchDriver(window),
                _ => _driver
            });
            Wait(p.StartDelayMs, token);
            var pointer = new PointerResolver(_windows, window, p);
            using var timer = new TimerResolution(p.Behavior != MacroBehaviorType.PressAndHold &&
                (p.SpamIntervalMs <= 16 || p.Actions.Any(a => a.ActionType == MacroActionType.Delay && a.DelayMs <= 16 || a.ActionType == MacroActionType.GroupStart && a.GapMs is > 0 and <= 16)));
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
                    else foreach (var step in StepGroups.Expand(p.Actions))
                    {
                        token.ThrowIfCancellationRequested();
                        pointer.EnsureAlive();
                        Execute(step, stepKeys.GetValueOrDefault(step, []), input, pointer, token);
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
    private static void Execute(MacroAction a, ushort[] keys, ExecutionInput input, PointerResolver pointer, CancellationToken token)
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
                input.Position(x, y); break;
            case MacroActionType.MouseScroll: input.Scroll(a.ScrollAmount); break;
            case MacroActionType.Text: input.Text(a.Text, token); break;
        }
    }
    private static void Wait(int milliseconds, CancellationToken token)
    {
        if (milliseconds > 0 && token.WaitHandle.WaitOne(milliseconds)) token.ThrowIfCancellationRequested();
        token.ThrowIfCancellationRequested();
    }
    public async ValueTask DisposeAsync() => await StopAsync();
}
