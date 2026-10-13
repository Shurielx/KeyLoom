using System.Runtime.InteropServices;

namespace Keyloom.Engine;

internal sealed class TimerResolution : IDisposable
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint period);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint period);
    private readonly bool _enabled;
    public TimerResolution(bool enabled) => _enabled = enabled && timeBeginPeriod(1) == 0;
    public void Dispose() { if (_enabled) timeEndPeriod(1); }
}
