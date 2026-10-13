using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using Keyloom.Models;

namespace Keyloom.Recording;

public sealed class MacroRecorder : IDisposable
{
    private static readonly List<MacroRecorder> RetainedCallbacks = new();
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int key);
    [StructLayout(LayoutKind.Sequential)] private struct POINT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint key, scan, flags, time; public UIntPtr extra; }
    [StructLayout(LayoutKind.Sequential)] private struct MouseData { public POINT point; public uint data, flags, time; public UIntPtr extra; }
    private readonly HookProc _keyboardCallback, _mouseCallback;
    private readonly Stopwatch _clock = new();
    private RecordingBuffer? _buffer;
    private IntPtr _keyboardHook, _mouseHook;
    private bool _stopRequested;
    private uint _stopKeyHeld;
    private int _wheelRemainder;
    public bool IsRecording { get; private set; }
    public bool IsPaused { get; private set; }
    public bool HasInstalledHooks => _keyboardHook != IntPtr.Zero || _mouseHook != IntPtr.Zero;
    public int Count => _buffer?.Count ?? 0;
    public long ElapsedMilliseconds => _clock.ElapsedMilliseconds;
    public string RemovalWarning { get; private set; } = "";
    public event Action<string>? StopRequested;
    public MacroRecorder() { _keyboardCallback = KeyboardHook; _mouseCallback = MouseHook; }
    public void Start(RecordingOptions options, int maxSteps = Engine.ProfileValidator.MaxActions)
    {
        if (IsRecording) throw new InvalidOperationException("Recording is already active.");
        if (RetainedCallbacks.Count > 0) throw new InvalidOperationException("Previous recording hooks could not be released. Restart Keyloom before recording again.");
        if (!options.MouseMovement && !options.MouseButtons && !options.Keyboard) throw new ArgumentException("Choose at least one input to record.");
        if (options.MovementIntervalMs is < 8 or > 1000 || maxSteps < 16) throw new ArgumentException("Invalid recording settings.");
        _buffer = new(options, maxSteps);
        _stopRequested = false; _stopKeyHeld = 0; _wheelRemainder = 0; IsPaused = false;
        try
        {
            var module = GetModuleHandle(null);
            _keyboardHook = SetWindowsHookEx(13, _keyboardCallback, module, 0);
            if (_keyboardHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not start keyboard recording.");
            if (options.MouseMovement || options.MouseButtons)
            {
                _mouseHook = SetWindowsHookEx(14, _mouseCallback, module, 0);
                if (_mouseHook == IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows could not start mouse recording.");
            }
            _clock.Restart(); IsRecording = true;
        }
        catch { RemoveHooks(); throw; }
    }
    public void TogglePause()
    {
        if (!IsRecording || _stopRequested) return;
        if (IsPaused) { _clock.Start(); IsPaused = false; }
        else { _clock.Stop(); _buffer!.Pause(_clock.ElapsedMilliseconds); IsPaused = true; }
    }
    public IReadOnlyList<MacroAction> Stop()
    {
        IsRecording = false; _clock.Stop(); RemoveHooks();
        return _buffer?.Complete(_clock.ElapsedMilliseconds) ?? [];
    }
    private IntPtr KeyboardHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || !IsRecording) return CallNextHookEx(_keyboardHook, code, message, data);
        try
        {
            var k = Marshal.PtrToStructure<KeyboardData>(data);
            if ((k.flags & 0x10) != 0) return CallNextHookEx(_keyboardHook, code, message, data);
            int msg = message.ToInt32();
            bool down = msg is 0x100 or 0x104, up = msg is 0x101 or 0x105;
            bool emergency = k.key == 0x7B && (GetAsyncKeyState(0x11) & 0x8000) != 0 && (GetAsyncKeyState(0x10) & 0x8000) != 0;
            if (k.key == 0x77 || emergency || k.key == _stopKeyHeld)
            {
                if (down) _stopKeyHeld = k.key;
                if (up) RequestStop("");
                return new IntPtr(1);
            }
            if (!_stopRequested && !IsPaused && (down || up))
            {
                ushort key = (ushort)(k.key switch
                {
                    0x10 => k.scan == 0x36 ? 0xA1u : 0xA0u,
                    0x11 => (k.flags & 1) != 0 ? 0xA3u : 0xA2u,
                    0x12 => (k.flags & 1) != 0 ? 0xA5u : 0xA4u,
                    _ => k.key
                });
                if (!OwnWindow(GetForegroundWindow()) || (up && _buffer!.HoldsKey(key)))
                {
                    var mapped = KeyInterop.KeyFromVirtualKey(key);
                    if (mapped != Key.None) _buffer!.Key(key, mapped.ToString(), down, _clock.ElapsedMilliseconds);
                }
                CheckLimit();
            }
        }
        catch (Exception ex) { RequestStop("Recording stopped: " + ex.Message); }
        return CallNextHookEx(_keyboardHook, code, message, data);
    }
    private IntPtr MouseHook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0 || !IsRecording || IsPaused || _stopRequested) return CallNextHookEx(_mouseHook, code, message, data);
        try
        {
            var m = Marshal.PtrToStructure<MouseData>(data);
            if ((m.flags & 1) == 0 && !OwnWindow(WindowFromPoint(m.point)))
            {
                long time = _clock.ElapsedMilliseconds;
                int msg = message.ToInt32(), x = m.point.x, y = m.point.y;
                if (msg == 0x200) _buffer!.Movement(x, y, time);
                else if (msg == 0x20A)
                {
                    _wheelRemainder += (short)(m.data >> 16);
                    int notches = _wheelRemainder / 120; _wheelRemainder %= 120;
                    _buffer!.Wheel(notches, x, y, time);
                }
                else
                {
                    string? button = msg switch
                    {
                        0x201 or 0x202 => "Left", 0x204 or 0x205 => "Right",
                        0x207 or 0x208 => "Middle", 0x20B or 0x20C => (m.data >> 16) == 1 ? "X1" : "X2",
                        _ => null
                    };
                    if (button != null) _buffer!.Button(button, msg is 0x201 or 0x204 or 0x207 or 0x20B, x, y, time);
                }
                CheckLimit();
            }
        }
        catch (Exception ex) { RequestStop("Recording stopped: " + ex.Message); }
        return CallNextHookEx(_mouseHook, code, message, data);
    }
    private static bool OwnWindow(IntPtr window)
    {
        if (window == IntPtr.Zero) return false;
        GetWindowThreadProcessId(window, out uint process); return process == Environment.ProcessId;
    }
    private void CheckLimit() { if (_buffer!.LimitReached) RequestStop("Recording reached the sequence step limit."); }
    private void RequestStop(string reason)
    {
        if (_stopRequested) return;
        _stopRequested = true; StopRequested?.Invoke(reason);
    }
    private void RemoveHooks()
    {
        Release(ref _keyboardHook); Release(ref _mouseHook);
        RemovalWarning = HasInstalledHooks ? "Windows could not release recording hooks. Restart Keyloom." : "";
        if (HasInstalledHooks && !RetainedCallbacks.Contains(this)) RetainedCallbacks.Add(this);
        if (!HasInstalledHooks) RetainedCallbacks.Remove(this);
        static void Release(ref IntPtr hook)
        {
            if (hook == IntPtr.Zero) return;
            if (UnhookWindowsHookEx(hook) || Marshal.GetLastWin32Error() is 6 or 1404) hook = IntPtr.Zero;
        }
    }
    public void Dispose() { Stop(); GC.KeepAlive(_keyboardCallback); GC.KeepAlive(_mouseCallback); }
}
