using System.Windows;
using Keyloom.Services;

namespace Keyloom.Views;

public partial class SettingsDialog : Window
{
    public SettingsDialog()
    {
        InitializeComponent();
        Backdrop.Attach(this);
        ExperimentalBox.IsChecked = AppSettings.Current.ShowExperimental;
        VersionText.Text = $"Keyloom {AppVersion.Display}";
    }
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        new AppSettings { ShowExperimental = ExperimentalBox.IsChecked == true }.Save();
        DialogResult = true;
    }
}
