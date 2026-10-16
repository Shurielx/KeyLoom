using System.Threading;
using System.Windows;

namespace Keyloom;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        Services.AppSettings.Load();
        L.Use(L.CodeFor(Services.AppSettings.Current.Language));
        L.Hook();
        _instance = new Mutex(true, "Local\\Keyloom", out bool first);
        if (!first)
        {
            MessageBox.Show(L.T("Keyloom is already running. Open it from the tray."), "Keyloom");
            Shutdown(); return;
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
