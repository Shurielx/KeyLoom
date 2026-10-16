using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace Keyloom.Views;

/// <summary>
/// Shows dialogs inside the window over a dimmed backdrop, instead of separate system windows.
/// Content closes itself with <see cref="Close"/>; IDisposable content is disposed once it has gone.
/// </summary>
public sealed class SheetLayer : Grid
{
    private sealed record Open(FrameworkElement Content, Grid Layer, TaskCompletionSource<bool> Done, IInputElement? Focus);
    private readonly List<Open> _open = new();
    public SheetLayer()
    {
        Visibility = Visibility.Collapsed;
        SizeChanged += (_, _) => { foreach (var open in _open) Fit(open.Layer.Children[1] as FrameworkElement); };
    }
    /// <summary>
    /// Keeps a sheet inside the window. Set by hand, not bound to ActualWidth: a binding made while the layer was collapsed
    /// read 0 and kept it, so a sheet opened a second time had no size and only the backdrop showed.
    /// </summary>
    private void Fit(FrameworkElement? card)
    {
        if (card == null) return;
        var room = new Size(ActualWidth > 0 ? ActualWidth : (Parent as FrameworkElement)?.ActualWidth ?? 0, ActualHeight > 0 ? ActualHeight : (Parent as FrameworkElement)?.ActualHeight ?? 0);
        card.MaxWidth = room.Width > 48 ? room.Width - 48 : double.PositiveInfinity;
        card.MaxHeight = room.Height > 48 ? room.Height - 48 : double.PositiveInfinity;
    }
    public bool IsOpen => _open.Count > 0;
    public FrameworkElement? Top => _open.Count > 0 ? _open[^1].Content : null;
    public Task<bool> ShowAsync(FrameworkElement content, double width = 440)
    {
        var scrim = new Border();
        scrim.SetResourceReference(Border.BackgroundProperty, "ScrimBrush");
        // Closes on release, and only for a press that began on the backdrop. Closing on the press let the release
        // fall through to whatever lay underneath, like the route card, which opened the sheet straight back up.
        bool pressed = false;
        scrim.MouseLeftButtonDown += (_, e) => { pressed = true; e.Handled = true; };
        scrim.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            if (pressed) Close(content, false);
            pressed = false;
        };
        var surface = new Border { Child = content, BorderThickness = new(1), CornerRadius = new(12), Padding = new(18, 16, 18, 16) };
        surface.SetResourceReference(Border.BackgroundProperty, "SheetBrush");
        surface.SetResourceReference(Border.BorderBrushProperty, "HairlineBrush");
        // The shadow is its own layer behind the sheet; text inside an element with an effect would lose ClearType.
        var shadow = new Border { CornerRadius = new(12), IsHitTestVisible = false };
        shadow.SetResourceReference(Border.BackgroundProperty, "SheetBrush");
        shadow.SetResourceReference(EffectProperty, "SheetShadow");
        var card = new Grid
        {
            Children = { shadow, surface }, Width = width,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Margin = new(24),
            RenderTransformOrigin = new(.5, .5), RenderTransform = new ScaleTransform(.96, .96)
        };
        RenderOptions.SetClearTypeHint(card, ClearTypeHint.Enabled);
        KeyboardNavigation.SetTabNavigation(card, KeyboardNavigationMode.Cycle);
        KeyboardNavigation.SetControlTabNavigation(card, KeyboardNavigationMode.Cycle);
        var layer = new Grid { Children = { scrim, card } };
        Fit(card);
        var done = new TaskCompletionSource<bool>();
        _open.Add(new(content, layer, done, Keyboard.FocusedElement));
        Children.Add(layer);
        Visibility = Visibility.Visible;
        var ease = new QuarticEase { EasingMode = EasingMode.EaseOut };
        scrim.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { FillBehavior = FillBehavior.Stop });
        card.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(200)) { FillBehavior = FillBehavior.Stop });
        var grow = new DoubleAnimation(.96, 1, TimeSpan.FromMilliseconds(320)) { EasingFunction = ease };
        card.RenderTransform.BeginAnimation(ScaleTransform.ScaleXProperty, grow);
        card.RenderTransform.BeginAnimation(ScaleTransform.ScaleYProperty, grow);
        content.Dispatcher.BeginInvoke(() => content.MoveFocus(new TraversalRequest(FocusNavigationDirection.First)), System.Windows.Threading.DispatcherPriority.Input);
        return done.Task;
    }
    /// <summary>Closes the sheet that holds this content. Returns false when it is not shown in a sheet.</summary>
    public static bool Close(FrameworkElement content, bool result)
    {
        for (DependencyObject? d = content; d != null; d = VisualTreeHelper.GetParent(d) ?? LogicalTreeHelper.GetParent(d))
            if (d is SheetLayer layer) return layer.CloseSheet(content, result);
        return false;
    }
    public bool CloseTop(bool result) => _open.Count > 0 && CloseSheet(_open[^1].Content, result);
    /// <summary>The sheet layer of the window that holds this element.</summary>
    public static SheetLayer? For(DependencyObject element) => Window.GetWindow(element) is { } window ? Find(window) : null;
    private static SheetLayer? Find(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is SheetLayer layer) return layer;
            if (Find(child) is { } found) return found;
        }
        return null;
    }
    /// <summary>Asks a yes/no question in a sheet.</summary>
    public Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false)
    {
        var panel = new StackPanel();
        var heading = new TextBlock { Text = title, FontSize = 15, Margin = new(0, 0, 0, 6) };
        heading.SetResourceReference(StyleProperty, "SectionTitle");
        var body = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 18) };
        body.SetResourceReference(StyleProperty, "Muted");
        var cancel = new Button { Content = L.T("Cancel"), MinWidth = 84, Margin = new(0, 0, 8, 0) };
        var ok = new Button { Content = confirm, MinWidth = 84 };
        ok.SetResourceReference(StyleProperty, danger ? "DangerButton" : "PrimaryButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } };
        panel.Children.Add(heading); panel.Children.Add(body); panel.Children.Add(buttons);
        cancel.Click += (_, _) => Close(panel, false);
        ok.Click += (_, _) => Close(panel, true);
        return ShowAsync(panel, 380);
    }
    private bool CloseSheet(FrameworkElement content, bool result)
    {
        int index = _open.FindIndex(o => o.Content == content);
        if (index < 0) return false;
        var open = _open[index];
        _open.RemoveAt(index);
        open.Layer.IsHitTestVisible = false;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(150));
        fade.Completed += (_, _) =>
        {
            Children.Remove(open.Layer);
            if (_open.Count == 0) Visibility = Visibility.Collapsed;
        };
        open.Layer.BeginAnimation(OpacityProperty, fade);
        if (content is IDisposable disposable) disposable.Dispose();
        open.Focus?.Focus();
        open.Done.TrySetResult(result);
        return true;
    }
}
