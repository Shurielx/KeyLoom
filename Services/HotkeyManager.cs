using System.Runtime.InteropServices;
using System.Windows.Interop;
using Keyloom.Models;

namespace Keyloom.Services;

public sealed class HotkeyManager : IDisposable
{
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    private readonly IntPtr _hwnd;
    private readonly HwndSource? _source;
    private readonly List<int> _ids = new();
    private readonly Dictionary<int, MacroProfile> _profiles = new();
    private const int StopId = 2;
    public event Action<MacroProfile>? ProfileTriggered;
    public event Action? StopTriggered;

    public HotkeyManager(IntPtr hwnd)
    {
        _hwnd = hwnd;
        _source = HwndSource.FromHwnd(hwnd);
        _source?.AddHook(Hook);
    }
    public string RegisterProfiles(IEnumerable<MacroProfile> profiles)
    {
        UnregisterAll();
        var errors = new List<string>();
        if (!Register(StopId, Shortcut.AppModifiers, Shortcut.StopKey)) errors.Add(L.T("Stop shortcut is unavailable. Use Stop all or the tray menu."));
        int id = 100;
        foreach (var profile in profiles)
        {
            profile.HotkeyStatus = "";
            if (profile.PrimaryVk == 0) continue;
            if (Shortcut.IsReserved(profile.PrimaryModifiers, profile.PrimaryVk))
                profile.HotkeyStatus = L.T("Reserved app shortcut");
            else if (Register(id, profile.PrimaryModifiers, profile.PrimaryVk))
                _profiles[id] = profile;
            else profile.HotkeyStatus = L.T("Shortcut is already in use");
            id++;
        }
        return string.Join(" ", errors);
    }
    private bool Register(int id, uint modifiers, uint key)
    {
        if (!RegisterHotKey(_hwnd, id, modifiers | 0x4000, key)) return false;
        _ids.Add(id); return true;
    }
    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message != 0x0312) return IntPtr.Zero;
        int id = wParam.ToInt32();
        if (id == StopId) StopTriggered?.Invoke();
        else if (_profiles.TryGetValue(id, out var profile)) ProfileTriggered?.Invoke(profile);
        else return IntPtr.Zero;
        handled = true; return IntPtr.Zero;
    }
    private void UnregisterAll()
    {
        foreach (int id in _ids) UnregisterHotKey(_hwnd, id);
        _ids.Clear(); _profiles.Clear();
    }
    public void Dispose() { UnregisterAll(); _source?.RemoveHook(Hook); }
}
