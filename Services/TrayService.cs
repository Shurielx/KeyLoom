using System.Drawing;
using System.Windows;
using System.Windows.Forms;

namespace Keyloom.Services;

public sealed class TrayService : IDisposable
{
    private readonly NotifyIcon _icon;
    private readonly Icon _image;
    private readonly ContextMenuStrip _menu;
    public TrayService(Action open, Action stop, Action exit)
    {
        var resource = System.Windows.Application.GetResourceStream(new Uri("pack://application:,,,/Keyloom;component/Assets/keyloom.ico"));
        using var stream = resource!.Stream;
        using var original = new Icon(stream);
        _image = (Icon)original.Clone();
        _menu = new ContextMenuStrip();
        _menu.Items.Add("Open Keyloom", null, (_, _) => open());
        _menu.Items.Add("Stop all macros", null, (_, _) => stop());
        _menu.Items.Add(new ToolStripSeparator());
        _menu.Items.Add("Exit", null, (_, _) => exit());
        _icon = new() { Icon = _image, Text = "Keyloom", Visible = true, ContextMenuStrip = _menu };
        _icon.DoubleClick += (_, _) => open();
    }
    public void Dispose() { _icon.Visible = false; _icon.Dispose(); _menu.Dispose(); _image.Dispose(); }
}
