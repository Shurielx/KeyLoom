using System.Threading;
using System.Windows;

namespace Keyloom;

public partial class App : Application
{
    private Mutex? _instance;
    /// <summary>True when this Keyloom was started by an update.</summary>
    public static bool Updated { get; private set; }
    private static bool WaitForOld(Mutex instance)
    {
        try { return instance.WaitOne(TimeSpan.FromSeconds(20)); }
        catch (AbandonedMutexException) { return true; }
    }
    protected override void OnStartup(StartupEventArgs e)
    {
        Services.AppSettings.Load();
        L.Use(L.CodeFor(Services.AppSettings.Current.Language));
        L.Hook();
        _instance = new Mutex(true, "Local\\Keyloom", out bool first);
        // After an update the old Keyloom is still closing; wait for it instead of saying it is already running.
        if (!first && e.Args.Contains(Services.UpdateService.UpdatedArgument)) first = WaitForOld(_instance);
        if (!first)
        {
            MessageBox.Show(L.T("Keyloom is already running. Open it from the tray."), "Keyloom");
            Shutdown(); return;
        }
        Updated = e.Args.Contains(Services.UpdateService.UpdatedArgument);
        Services.UpdateService.CleanUp();
        if (Services.UpdateService.IsInstalled && !System.IO.File.Exists(Services.UpdateService.StartMenuShortcut))
        {
            try { Services.UpdateService.CreateShortcut(Services.UpdateService.InstalledExe); }
            catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException) { }
        }
        Services.ThemeManager.Apply(Services.AppSettings.Current.Theme);
        Services.ClickSound.Hook();
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
