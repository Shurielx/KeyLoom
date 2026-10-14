using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>Draws a macro's icon: a built-in symbol, one or two characters, or a stored picture.</summary>
public sealed class MacroIcon : Decorator
{
    public static readonly DependencyProperty KindProperty = Register(nameof(Kind), MacroIconKind.Auto);
    public static readonly DependencyProperty GlyphProperty = Register(nameof(Glyph), "");
    public static readonly DependencyProperty ImageProperty = Register(nameof(Image), "");
    public static readonly DependencyProperty BehaviorProperty = Register(nameof(Behavior), MacroBehaviorType.ClickSpam);
    public static readonly DependencyProperty TargetProperty = Register(nameof(Target), TargetInputType.Mouse);
    public static readonly DependencyProperty ForegroundProperty = System.Windows.Documents.TextElement.ForegroundProperty.AddOwner(typeof(MacroIcon),
        new FrameworkPropertyMetadata(Brushes.Black, FrameworkPropertyMetadataOptions.Inherits));
    public static readonly DependencyProperty SymbolSizeProperty = Register(nameof(SymbolSize), 18.0);
    public MacroIconKind Kind { get => (MacroIconKind)GetValue(KindProperty); set => SetValue(KindProperty, value); }
    public string Glyph { get => (string)GetValue(GlyphProperty); set => SetValue(GlyphProperty, value); }
    public string Image { get => (string)GetValue(ImageProperty); set => SetValue(ImageProperty, value); }
    public MacroBehaviorType Behavior { get => (MacroBehaviorType)GetValue(BehaviorProperty); set => SetValue(BehaviorProperty, value); }
    public TargetInputType Target { get => (TargetInputType)GetValue(TargetProperty); set => SetValue(TargetProperty, value); }
    public Brush Foreground { get => (Brush)GetValue(ForegroundProperty); set => SetValue(ForegroundProperty, value); }
    public double SymbolSize { get => (double)GetValue(SymbolSizeProperty); set => SetValue(SymbolSizeProperty, value); }
    public MacroIcon() => Loaded += (_, _) => Rebuild();
    private static DependencyProperty Register<T>(string name, T fallback) =>
        DependencyProperty.Register(name, typeof(T), typeof(MacroIcon), new PropertyMetadata(fallback, (d, _) => ((MacroIcon)d).Rebuild()));
    public void Show(MacroProfile p)
    {
        Kind = p.IconKind; Glyph = p.IconGlyph; Image = p.IconImage; Behavior = p.Behavior; Target = p.TargetType;
    }
    /// <summary>The icon that is actually shown, after falling back for empty glyphs or missing pictures.</summary>
    public static MacroIconKind Effective(MacroIconKind kind, string glyph, string image) => kind switch
    {
        MacroIconKind.Glyph when string.IsNullOrWhiteSpace(glyph) => MacroIconKind.Auto,
        MacroIconKind.Image when string.IsNullOrEmpty(image) => MacroIconKind.Auto,
        _ => kind
    };
    public static string SymbolKey(MacroIconKind kind, MacroBehaviorType behavior, TargetInputType target) => kind switch
    {
        MacroIconKind.Mouse => "IconMouse",
        MacroIconKind.Keyboard => "IconKeyboard",
        MacroIconKind.Cursor => "IconCursorMove",
        _ => behavior == MacroBehaviorType.Sequence ? "IconBolt" : target == TargetInputType.Keyboard ? "IconKeyboard" : "IconMouse"
    };
    private void Rebuild()
    {
        var kind = Effective(Kind, Glyph, Image);
        if (kind == MacroIconKind.Image && IconStore.Load(Image) is { } picture)
        {
            var image = new System.Windows.Controls.Image { Source = picture, Stretch = Stretch.UniformToFill };
            RenderOptions.SetBitmapScalingMode(image, BitmapScalingMode.HighQuality);
            var frame = new Border { Child = image, Margin = new(3) };
            frame.SizeChanged += (_, _) => ClipRounded(frame);
            Child = frame;
            return;
        }
        if (kind == MacroIconKind.Glyph)
        {
            var text = new TextBlock
            {
                Text = Glyph.Trim(), FontFamily = new FontFamily("Segoe UI Variable Display, Segoe UI"), FontWeight = FontWeights.Bold,
                FontSize = SymbolSize * (Glyph.Trim().Length > 1 ? 0.92 : 1.12), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Center
            };
            text.SetBinding(TextBlock.ForegroundProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
            Child = text;
            return;
        }
        if (kind == MacroIconKind.Image) kind = MacroIconKind.Auto;
        var path = new Path { Stretch = Stretch.Uniform, Width = SymbolSize, Height = SymbolSize, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        path.SetResourceReference(Path.DataProperty, SymbolKey(kind, Behavior, Target));
        path.SetBinding(Shape.FillProperty, new System.Windows.Data.Binding(nameof(Foreground)) { Source = this });
        Child = path;
    }
    private static void ClipRounded(Border border) =>
        border.Clip = new RectangleGeometry(new Rect(0, 0, border.ActualWidth, border.ActualHeight), 7, 7);
}
