using System.Windows;
using System.Windows.Input;
using System.Windows.Media;

namespace Keyloom.Views;

public partial class RecordingWindow : Window
{
    public event Action? StopRequested;
    public event Action? PauseRequested;
    public RecordingWindow()
    {
        InitializeComponent();
        Backdrop.Attach(this);
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + 18;
    }
    public void Countdown(int seconds) { StateText.Text = $"Starting in {seconds}..."; }
    public void Status(bool paused, long elapsed, int steps)
    {
        PauseButton.IsEnabled = true;
        PauseButton.Content = paused ? "Resume" : "Pause";
        StopButton.Content = "Stop";
        StateText.Text = paused ? "Recording paused" : "Recording";
        DetailsText.Text = $"{TimeSpan.FromMilliseconds(elapsed):mm\\:ss}   ·   {steps:N0} steps   ·   F8 to stop";
        if (paused) RecordingDot.Fill = new SolidColorBrush(Color.FromRgb(176, 132, 40));
        else RecordingDot.SetResourceReference(System.Windows.Shapes.Shape.FillProperty, "RecordBrush");
        RecordingHalo.Visibility = paused ? Visibility.Hidden : Visibility.Visible;
    }
    private void Stop_Click(object sender, RoutedEventArgs e) => StopRequested?.Invoke();
    private void Pause_Click(object sender, RoutedEventArgs e) => PauseRequested?.Invoke();
    private void Drag_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }
}
