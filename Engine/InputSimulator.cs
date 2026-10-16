using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Keyloom.Engine;

public interface IInputDriver
{
    void Key(ushort key, bool down);
    void Mouse(string button, bool down);
    void Move(int x, int y);
    void Position(int x, int y);
    void Scroll(int notches);
    void Character(char value, bool down);
}

public sealed class InputSimulator : IInputDriver
{
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
    [DllImport("user32.dll")] private static extern uint MapVirtualKey(uint code, uint type);
    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint type; public InputUnion data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT mouse;
        [FieldOffset(0)] public KEYBDINPUT key;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, flags, time;
        public UIntPtr extraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    {
        public ushort vk, scan;
        public uint flags, time;
        public UIntPtr extraInfo;
    }
    private readonly INPUT[] _buffer = new INPUT[1];
    private static readonly int InputSize = Marshal.SizeOf<INPUT>();
    public void Key(ushort key, bool down)
    {
        uint scan = MapVirtualKey(key, 4);
        uint flags = down ? 0u : 2u;
        if ((scan & 0xFF00) == 0xE000 || key is 0xA3 or 0xA5 or 0x5B or 0x5C) flags |= 1;
        Send(new INPUT { type = 1, data = new InputUnion { key = new KEYBDINPUT { vk = key, scan = (ushort)(scan & 0xFF), flags = flags } } });
    }
    public void Character(char value, bool down) => Send(new INPUT
    {
        type = 1, data = new InputUnion { key = new KEYBDINPUT { scan = value, flags = 4u | (down ? 0u : 2u) } }
    });
    public void Mouse(string button, bool down)
    {
        uint flags = button switch
        {
            "Left" => down ? 2u : 4u, "Right" => down ? 8u : 16u,
            "Middle" => down ? 32u : 64u, "X1" or "X2" => down ? 128u : 256u,
            _ => throw new ArgumentException(L.T("Unknown mouse button."))
        };
        SendMouse(flags, button == "X1" ? 1u : button == "X2" ? 2u : 0u);
    }
    public void Move(int x, int y) => SendMouse(1, x: x, y: y);
    public void Position(int x, int y)
    {
        int left = GetSystemMetrics(76), top = GetSystemMetrics(77);
        int width = Math.Max(1, GetSystemMetrics(78)), height = Math.Max(1, GetSystemMetrics(79));
        SendMouse(0xC001, x: NormalizePosition(x, left, width), y: NormalizePosition(y, top, height));
    }
    public static int NormalizePosition(int value, int origin, int size) =>
        (int)Math.Clamp((2 * ((long)value - origin) + 1) * 65536 / (2L * Math.Max(1, size)), 0, 65535);
    public void Scroll(int notches) => SendMouse(0x0800, unchecked((uint)(notches * 120)));
    private void SendMouse(uint flags, uint data = 0, int x = 0, int y = 0) => Send(new INPUT
    {
        data = new InputUnion { mouse = new MOUSEINPUT { dx = x, dy = y, mouseData = data, flags = flags } }
    });
    private void Send(INPUT input)
    {
        _buffer[0] = input;
        if (SendInput(1, _buffer, InputSize) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), L.T("Windows rejected macro input. Check the target window and its permissions."));
    }
}
