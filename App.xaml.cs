using System.Threading;
using System.Windows;

namespace Keyloom;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        _instance = new Mutex(true, "Local\\Keyloom", out bool first);
        if (!first)
        {
            MessageBox.Show("Keyloom is already running. Open it from the tray.", "Keyloom");
            Shutdown(); return;
        }
        Services.AppSettings.Load();
        Services.ThemeManager.Apply(Services.AppSettings.Current.Theme);
        base.OnStartup(e);
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
