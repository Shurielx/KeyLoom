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
        L.Apply(this);
        Backdrop.Attach(this);
        Left = SystemParameters.WorkArea.Left + (SystemParameters.WorkArea.Width - Width) / 2;
        Top = SystemParameters.WorkArea.Top + 18;
    }
    public void Countdown(int seconds) { StateText.Text = L.T($"Starting in {seconds}..."); }
    public void Status(bool paused, long elapsed, int steps)
    {
        PauseButton.IsEnabled = true;
        PauseButton.Content = paused ? L.T("Resume") : L.T("Pause");
        StopButton.Content = L.T("Stop");
        StateText.Text = paused ? L.T("Recording paused") : L.T("Recording");
        DetailsText.Text = $"{TimeSpan.FromMilliseconds(elapsed):mm\\:ss}   ·   " + L.N(steps, "{0} step", "{0} steps") + "   ·   " + L.T("F8 to stop");
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
