using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Keyloom.Services;

namespace Keyloom.Engine;

/// <summary>A key a script listens for while it runs, with the modifiers held with it: "1", "Ctrl+1", "F9".</summary>
public readonly record struct KeyChord(uint Modifiers, ushort Key)
{
    /// <summary>Ctrl+Shift+F12 stops every macro, so no script can take it.</summary>
    public static readonly KeyChord Stop = new(Shortcut.AppModifiers, (ushort)Shortcut.StopKey);

    /// <summary>Reads a key named as in press(): one key, with Ctrl, Alt, Shift or Win before it.</summary>
    /// <exception cref="ArgumentException">Not a key, or more than one key besides the modifiers.</exception>
    public static KeyChord Parse(string text)
    {
        uint modifiers = 0;
        ushort key = 0;
        foreach (ushort vk in KeyResolver.Resolve(text))
        {
            uint modifier = Modifier(vk);
            if (modifier != 0) { modifiers |= modifier; continue; }
            if (key != 0) throw new ArgumentException(L.T($"\"{text}\" has more than one key. Use one key, with Ctrl, Alt, Shift or Win before it if you like, such as \"Ctrl+1\"."));
            key = vk;
        }
        if (key == 0) throw new ArgumentException(L.T($"\"{text}\" has no key besides Ctrl, Alt, Shift or Win. Add one, such as \"Ctrl+1\"."));
        return new(modifiers, key);
    }
    /// <summary>The modifier a key is, as in <see cref="Shortcut"/>, or 0 for any other key.</summary>
    public static uint Modifier(uint vk) => vk switch
    {
        0x10 or 0xA0 or 0xA1 => Shortcut.Shift,
        0x11 or 0xA2 or 0xA3 => Shortcut.Control,
        0x12 or 0xA4 or 0xA5 => Shortcut.Alt,
        0x5B or 0x5C => Shortcut.Win,
        _ => 0
    };
    public override string ToString() => Shortcut.ToText(Modifiers, Key);
}

/// <summary>Hears the keys a running script listens for. Keys the script itself presses do not count.</summary>
public interface IKeyListener : IDisposable
{
    /// <summary>The keys to hear, each with whether the app in front still gets it.</summary>
    void Listen(IReadOnlyDictionary<KeyChord, bool> passes);
    /// <summary>Set when a key was pressed; <see cref="TryTake"/> gives the presses in order.</summary>
    WaitHandle Pressed { get; }
    bool TryTake(out KeyChord chord);
}

/// <summary>
/// Listens with a low-level keyboard hook on a thread of its own, so it hears keys whatever app is in front.
/// Keys a script presses are marked as sent by a program and are let through unheard; holding a key down counts once.
/// </summary>
public sealed class KeyHook : IKeyListener
{
    private delegate IntPtr HookProc(int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr message, IntPtr data);
    [DllImport("user32.dll")] private static extern int GetMessage(out Msg message, IntPtr window, uint min, uint max);
    [DllImport("user32.dll")] private static extern bool PeekMessage(out Msg message, IntPtr window, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint thread, uint message, IntPtr w, IntPtr l);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
    [StructLayout(LayoutKind.Sequential)] private struct Msg { public IntPtr window; public uint message; public IntPtr w, l; public uint time; public int x, y; }
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint key, scan, flags, time; public UIntPtr extra; }
    private const uint Injected = 0x10, Quit = 0x12;

    private readonly HookProc _callback;
    private readonly Thread _thread;
    private readonly ConcurrentQueue<KeyChord> _pressed = new();
    private readonly AutoResetEvent _signal = new(false);
    private readonly HashSet<uint> _down = [];
    private readonly HashSet<uint> _kept = [];
    private volatile IReadOnlyDictionary<KeyChord, bool> _passes = new Dictionary<KeyChord, bool>();
    private uint _modifiers, _threadId;
    private IntPtr _hook;

    public KeyHook()
    {
        _callback = Hook;
        using var ready = new ManualResetEventSlim();
        int error = 0;
        _thread = new Thread(() =>
        {
            _threadId = GetCurrentThreadId();
            _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
            if (_hook == IntPtr.Zero) error = Marshal.GetLastWin32Error();
            // Makes the thread's message queue now, so the quit Dispose posts is never lost.
            PeekMessage(out _, IntPtr.Zero, 0, 0, 0);
            ready.Set();
            if (_hook == IntPtr.Zero) return;
            while (GetMessage(out _, IntPtr.Zero, 0, 0) > 0) { }
            UnhookWindowsHookEx(_hook);
        }) { IsBackground = true, Name = "Keyloom script keys" };
        _thread.Start();
        ready.Wait();
        if (_hook == IntPtr.Zero) throw new Win32Exception(error, L.T("Windows did not let Keyloom listen for the script's keys."));
    }
    public WaitHandle Pressed => _signal;
    public void Listen(IReadOnlyDictionary<KeyChord, bool> passes) => _passes = new Dictionary<KeyChord, bool>(passes);
    public bool TryTake(out KeyChord chord) => _pressed.TryDequeue(out chord);

    private IntPtr Hook(int code, IntPtr message, IntPtr data)
    {
        if (code < 0) return CallNextHookEx(_hook, code, message, data);
        var k = Marshal.PtrToStructure<KeyboardData>(data);
        if ((k.flags & Injected) != 0) return CallNextHookEx(_hook, code, message, data);
        bool down = message.ToInt32() is 0x100 or 0x104;
        uint modifier = KeyChord.Modifier(k.key);
        if (modifier != 0)
        {
            _modifiers = down ? _modifiers | modifier : _modifiers & ~modifier;
            return CallNextHookEx(_hook, code, message, data);
        }
        if (!down)
        {
            _down.Remove(k.key);
            return _kept.Remove(k.key) ? new IntPtr(1) : CallNextHookEx(_hook, code, message, data);
        }
        if (!_passes.TryGetValue(new KeyChord(_modifiers, (ushort)k.key), out bool pass))
            return _kept.Contains(k.key) ? new IntPtr(1) : CallNextHookEx(_hook, code, message, data);
        // A key held down repeats; only the first press counts.
        if (_down.Add(k.key)) { _pressed.Enqueue(new KeyChord(_modifiers, (ushort)k.key)); _signal.Set(); }
        if (pass) return CallNextHookEx(_hook, code, message, data);
        _kept.Add(k.key);
        return new IntPtr(1);
    }
    public void Dispose()
    {
        if (_threadId != 0) PostThreadMessage(_threadId, Quit, IntPtr.Zero, IntPtr.Zero);
        _thread.Join(1000);
        _signal.Dispose();
        GC.KeepAlive(_callback);
    }
}
