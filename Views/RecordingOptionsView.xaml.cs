using System.Windows;
using System.Windows.Controls;
using Keyloom.Models;
using Keyloom.Recording;

namespace Keyloom.Views;

public partial class RecordingOptionsView : UserControl
{
    public RecordingOptions Options { get; private set; } = new();
    public RecordingOptionsView() => InitializeComponent();
    private void Start_Click(object sender, RoutedEventArgs e)
    {
        if (MovementBox.IsChecked != true && ButtonsBox.IsChecked != true && KeyboardBox.IsChecked != true)
        { ErrorText.Text = "Choose at least one input to record."; return; }
        if (!int.TryParse(SampleTextBox.Text, out int interval) || interval is < 8 or > 1000)
        { ErrorText.Text = "Mouse sample must be between 8 and 1000 ms."; return; }
        Options = new(MovementBox.IsChecked == true, ButtonsBox.IsChecked == true,
            KeyboardBox.IsChecked == true, TimingBox.IsChecked == true, InsertModeBox.SelectedIndex == 0, interval,
            (CoordinateSpace)Math.Max(0, SpaceBox.SelectedIndex), SimplifyBox.IsChecked == true);
        SheetLayer.Close(this, true);
    }
    private void Cancel_Click(object sender, RoutedEventArgs e) => SheetLayer.Close(this, false);
}
