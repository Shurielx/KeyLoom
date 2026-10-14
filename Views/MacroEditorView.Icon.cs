using System.Windows;
using System.Windows.Controls;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>Macro icon: a built-in symbol, a letter or number, or the person's own picture.</summary>
public partial class MacroEditorView
{
    private MacroIconKind _iconKind;
    private string _iconGlyph = "", _iconImage = "";
    private bool _loadingIcon;
    private void InitializeIcon()
    {
        _iconKind = Profile.IconKind; _iconGlyph = Profile.IconGlyph ?? ""; _iconImage = Profile.IconImage ?? "";
        _loadingIcon = true;
        GlyphTextBox.Text = _iconKind == MacroIconKind.Glyph ? _iconGlyph : "";
        _loadingIcon = false;
        SyncIconChoices();
    }
    private void Icon_Click(object sender, RoutedEventArgs e) => IconPopup.IsOpen = !IconPopup.IsOpen;
    private void SyncIconChoices()
    {
        _loadingIcon = true;
        ImageIconRadio.Visibility = _iconImage.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ImageIconPreview.Image = _iconImage;
        var choice = _iconKind switch
        {
            MacroIconKind.Mouse => MouseIconRadio, MacroIconKind.Keyboard => KeyboardIconRadio, MacroIconKind.Cursor => CursorIconRadio,
            MacroIconKind.Image => ImageIconRadio, MacroIconKind.Auto => AutoIconRadio, _ => null
        };
        foreach (var radio in new[] { AutoIconRadio, MouseIconRadio, KeyboardIconRadio, CursorIconRadio, ImageIconRadio }) radio.IsChecked = radio == choice;
        _loadingIcon = false;
        UpdateIconPreview();
    }
    private void UpdateIconPreview()
    {
        if (EditorIcon == null) return;
        var behavior = (MacroBehaviorType)Mode;
        var target = KeyboardTarget ? TargetInputType.Keyboard : TargetInputType.Mouse;
        EditorIcon.Behavior = AutoIconPreview.Behavior = behavior;
        EditorIcon.Target = AutoIconPreview.Target = target;
        EditorIcon.Glyph = _iconGlyph; EditorIcon.Image = _iconImage; EditorIcon.Kind = _iconKind;
    }
    private void IconChoice_Checked(object sender, RoutedEventArgs e)
    {
        if (_loadingIcon) return;
        _iconKind = sender == MouseIconRadio ? MacroIconKind.Mouse : sender == KeyboardIconRadio ? MacroIconKind.Keyboard
            : sender == CursorIconRadio ? MacroIconKind.Cursor : sender == ImageIconRadio ? MacroIconKind.Image : MacroIconKind.Auto;
        _loadingIcon = true; GlyphTextBox.Text = ""; _loadingIcon = false;
        UpdateIconPreview();
    }
    private void Glyph_Changed(object sender, TextChangedEventArgs e)
    {
        if (_loadingIcon) return;
        string glyph = GlyphTextBox.Text.Trim();
        if (glyph.Length > 0) { _iconGlyph = glyph; _iconKind = MacroIconKind.Glyph; }
        else if (_iconKind == MacroIconKind.Glyph) _iconKind = MacroIconKind.Auto;
        SyncIconChoices();
    }
    private void UploadIcon_Click(object sender, RoutedEventArgs e)
    {
        IconPopup.IsOpen = false;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Choose a macro icon",
            Filter = "Images|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.ico;*.tif;*.tiff|All files|*.*"
        };
        if (dialog.ShowDialog(Host) != true) return;
        Attempt(() => UseIconImage(dialog.FileName));
    }
    /// <summary>Copies a picture into the icon folder and selects it.</summary>
    public void UseIconImage(string file)
    {
        _iconImage = IconStore.Import(file);
        _iconKind = MacroIconKind.Image;
        _loadingIcon = true; GlyphTextBox.Text = ""; _loadingIcon = false;
        SyncIconChoices();
    }
    private void ApplyIcon(MacroProfile p)
    {
        var kind = MacroIcon.Effective(_iconKind, _iconGlyph, _iconImage);
        p.IconKind = kind;
        p.IconGlyph = kind == MacroIconKind.Glyph ? _iconGlyph : "";
        p.IconImage = kind == MacroIconKind.Image ? _iconImage : "";
    }
}
