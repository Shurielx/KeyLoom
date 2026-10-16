using System.Windows;
using System.Windows.Controls;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>
/// Chooses a macro icon: automatic, a built-in symbol, one or two characters, or the person's own picture.
/// Used by the editor and by the macro list. Raises <see cref="Changed"/> after every choice.
/// </summary>
public partial class IconPicker : UserControl
{
    private bool _loading;
    public MacroIconKind Kind { get; private set; }
    public string Glyph { get; private set; } = "";
    public string Image { get; private set; } = "";
    public event Action? Changed;
    /// <summary>Asks the host to close its popup, before the file dialog opens.</summary>
    public event Action? CloseRequested;
    /// <summary>Raised with a message when a picture can't be used.</summary>
    public event Action<string>? Failed;
    // The choices have no group name, so they group by their panel and two pickers never uncheck each other.
    public IconPicker() => InitializeComponent();
    /// <summary>Loads an icon without raising <see cref="Changed"/>. Behavior and target drive the automatic icon.</summary>
    public void Show(MacroIconKind kind, string glyph, string image, MacroBehaviorType behavior, TargetInputType target)
    {
        Kind = kind; Glyph = glyph ?? ""; Image = image ?? "";
        _loading = true;
        GlyphTextBox.Text = Kind == MacroIconKind.Glyph ? Glyph : "";
        _loading = false;
        SetAuto(behavior, target);
        Sync();
    }
    public void SetAuto(MacroBehaviorType behavior, TargetInputType target)
    {
        AutoIconPreview.Behavior = behavior; AutoIconPreview.Target = target;
    }
    /// <summary>Writes the chosen icon into a profile, falling back to automatic when a glyph or picture is empty.</summary>
    public void ApplyTo(MacroProfile p)
    {
        var kind = MacroIcon.Effective(Kind, Glyph, Image);
        p.IconKind = kind;
        p.IconGlyph = kind == MacroIconKind.Glyph ? Glyph : "";
        p.IconImage = kind == MacroIconKind.Image ? Image : "";
    }
    private void Sync()
    {
        _loading = true;
        ImageIconRadio.Visibility = Image.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ImageIconPreview.Image = Image;
        var choice = Kind switch
        {
            MacroIconKind.Mouse => MouseIconRadio, MacroIconKind.Keyboard => KeyboardIconRadio, MacroIconKind.Cursor => CursorIconRadio,
            MacroIconKind.Image => ImageIconRadio, MacroIconKind.Auto => AutoIconRadio, _ => null
        };
        foreach (var radio in new[] { AutoIconRadio, MouseIconRadio, KeyboardIconRadio, CursorIconRadio, ImageIconRadio }) radio.IsChecked = radio == choice;
        _loading = false;
    }
    private void Choice_Checked(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        Kind = sender == MouseIconRadio ? MacroIconKind.Mouse : sender == KeyboardIconRadio ? MacroIconKind.Keyboard
            : sender == CursorIconRadio ? MacroIconKind.Cursor : sender == ImageIconRadio ? MacroIconKind.Image : MacroIconKind.Auto;
        _loading = true; GlyphTextBox.Text = ""; _loading = false;
        Changed?.Invoke();
    }
    private void Glyph_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loading) return;
        string glyph = GlyphTextBox.Text.Trim();
        if (glyph.Length > 0) { Glyph = glyph; Kind = MacroIconKind.Glyph; }
        else if (Kind == MacroIconKind.Glyph) Kind = MacroIconKind.Auto;
        Sync();
        Changed?.Invoke();
    }
    private void Upload_Click(object sender, RoutedEventArgs e)
    {
        CloseRequested?.Invoke();
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a macro icon",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff|All files|*.*"
        };
        if (dialog.ShowDialog(Application.Current?.MainWindow) != true) return;
        try { UseImage(dialog.FileName); }
        catch (ArgumentException ex) { Failed?.Invoke(ex.Message); }
    }
    /// <summary>Copies a picture into the icon folder and selects it.</summary>
    public void UseImage(string file)
    {
        Image = IconStore.Import(file);
        Kind = MacroIconKind.Image;
        _loading = true; GlyphTextBox.Text = ""; _loading = false;
        Sync();
        Changed?.Invoke();
    }
}
