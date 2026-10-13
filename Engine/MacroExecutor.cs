using System.Diagnostics;
using Keyloom.Models;

namespace Keyloom.Engine;

public sealed class MacroExecutor(IInputDriver? driver = null) : IAsyncDisposable
{
    private readonly IInputDriver _driver = driver ?? new InputSimulator();
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
            var stepKeys = snapshot.Actions.Select(a => a.ActionType is MacroActionType.KeyPress or MacroActionType.KeyDown or MacroActionType.KeyUp ? KeyResolver.Resolve(a.KeyName) : Array.Empty<ushort>()).ToArray();
            _cts = new();
            var token = _cts.Token;
            Volatile.Write(ref _activeProfile, profile);
            ExecutionStateChanged?.Invoke(profile, true);
            _task = Task.Run(() => Run(snapshot, profile, keys, stepKeys, token));
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
    private void Run(MacroProfile p, MacroProfile original, ushort[] keys, ushort[][] stepKeys, CancellationToken token)
    {
        var input = new ExecutionInput(_driver);
        long count = 0;
        var progressClock = Stopwatch.StartNew();
        try
        {
            Wait(p.StartDelayMs, token);
            using var timer = new TimerResolution(p.Behavior != MacroBehaviorType.PressAndHold &&
                (p.SpamIntervalMs <= 16 || p.Actions.Any(a => a.ActionType == MacroActionType.Delay && a.DelayMs <= 16)));
            if (p.Behavior == MacroBehaviorType.PressAndHold)
            {
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
                    if (p.Behavior == MacroBehaviorType.ClickSpam)
                    {
                        if (p.TargetType == TargetInputType.Mouse) input.Click(p.TargetMouseButton);
                        else input.Press(keys);
                    }
                    else for (int i = 0; i < p.Actions.Count; i++)
                    {
                        token.ThrowIfCancellationRequested();
                        Execute(p.Actions[i], stepKeys[i], input, token);
                    }
                    count++;
                    if (progressClock.ElapsedMilliseconds >= 100)
                    {
                        ProgressChanged?.Invoke(original, count); progressClock.Restart();
                    }
                    if (p.RepeatCount != 0 && count == p.RepeatCount) break;
                    Wait(p.SpamIntervalMs, token);
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex) { ExecutionFailed?.Invoke(ex.Message); }
        finally
        {
            try { input.ReleaseOwned(); }
            catch (Exception ex) { ExecutionFailed?.Invoke(ex.Message); }
            Volatile.Write(ref _activeProfile, null);
            ProgressChanged?.Invoke(original, count);
            ExecutionStateChanged?.Invoke(original, false);
        }
    }
    private static void Execute(MacroAction a, ushort[] keys, ExecutionInput input, CancellationToken token)
    {
        switch (a.ActionType)
        {
            case MacroActionType.MouseClick: input.Click(a.MouseButton); break;
            case MacroActionType.MouseDown: input.MouseDown(a.MouseButton); break;
            case MacroActionType.MouseUp: input.MouseUp(a.MouseButton); break;
            case MacroActionType.KeyPress: input.Press(keys); break;
            case MacroActionType.KeyDown: foreach (var key in keys) input.KeyDown(key); break;
            case MacroActionType.KeyUp: foreach (var key in keys.Reverse()) input.KeyUp(key); break;
            case MacroActionType.Delay: Wait(a.DelayMs, token); break;
            case MacroActionType.MouseMove: input.Move(a.X, a.Y); break;
            case MacroActionType.MousePosition: input.Position(a.X, a.Y); break;
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
