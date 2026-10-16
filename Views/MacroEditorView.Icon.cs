using System.Windows;
using Keyloom.Models;

namespace Keyloom.Views;

/// <summary>Macro icon: a built-in symbol, a letter or number, or the person's own picture. Chosen with <see cref="IconPicker"/>.</summary>
public partial class MacroEditorView
{
    private void InitializeIcon()
    {
        IconChooser.Show(Profile.IconKind, Profile.IconGlyph ?? "", Profile.IconImage ?? "", Profile.Behavior, Profile.TargetType);
        UpdateIconPreview();
    }
    private void Icon_Click(object sender, RoutedEventArgs e) => IconPopup.IsOpen = !IconPopup.IsOpen;
    private void UpdateIconPreview()
    {
        if (EditorIcon == null || IconChooser == null) return;
        var behavior = (MacroBehaviorType)Mode;
        var target = KeyboardTarget ? TargetInputType.Keyboard : TargetInputType.Mouse;
        IconChooser.SetAuto(behavior, target);
        EditorIcon.Behavior = behavior; EditorIcon.Target = target;
        EditorIcon.Glyph = IconChooser.Glyph; EditorIcon.Image = IconChooser.Image; EditorIcon.Kind = IconChooser.Kind;
    }
    private void IconChooser_Changed() => UpdateIconPreview();
    private void IconChooser_CloseRequested() => IconPopup.IsOpen = false;
    private void IconChooser_Failed(string message) => Say(message, true);
    /// <summary>Copies a picture into the icon folder and selects it.</summary>
    public void UseIconImage(string file) => Attempt(() => IconChooser.UseImage(file));
    private void ApplyIcon(MacroProfile p) => IconChooser.ApplyTo(p);
}
