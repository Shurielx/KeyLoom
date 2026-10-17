using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Keyloom.Engine;
using Keyloom.Services;

namespace Keyloom.Views;

/// <summary>Experimental: the keys a script listens for while it runs, and the buttons it shows here.</summary>
public partial class MacroEditorView
{
    private string _controlsShape = "";
    /// <summary>The macro's own shortcut, which no key in the script may take.</summary>
    private KeyChord? OwnChord => _key is > 0 and <= 0xFF ? new KeyChord(_modifiers, (ushort)_key) : null;

    /// <summary>
    /// Each key the script listens for with on(): its key to change, whether the app in front gets it too and a way to
    /// try it. Then the script's buttons. Rebuilt only when one of them changes.
    /// </summary>
    private void RefreshScriptControls()
    {
        if (ScriptKeyList == null) return;
        var (keys, buttons) = ScriptControls.Read(ScriptBox.Text, OwnChord);
        string shape = string.Join("\n", keys.Select(k => k.Shape).Concat(buttons.Select(b => b.Shape)));
        if (shape == _controlsShape) return;
        _controlsShape = shape;
        ScriptKeyList.Children.Clear();
        for (int i = 0; i < keys.Count; i++) ScriptKeyList.Children.Add(KeyRow(keys[i], i));
        if (buttons.Count > 0)
        {
            var wrap = new WrapPanel { Margin = new Thickness(0, keys.Count > 0 ? 4 : 0, 0, 0) };
            foreach (var b in buttons)
            {
                var button = new Button
                {
                    Padding = new Thickness(10, 4, 10, 4), Margin = new Thickness(0, 0, 6, 6),
                    ToolTip = L.T($"Test run: plays the script's first run, then what button(\"{b.Label}\") on line {b.Line} does")
                };
                var content = new StackPanel { Orientation = Orientation.Horizontal };
                content.Children.Add(new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IconPlay"), Style = (Style)FindResource("AccentIcon"), Width = 10, Height = 10, Margin = new Thickness(0, 0, 7, 0) });
                content.Children.Add(new TextBlock { Text = b.Label });
                button.Content = content;
                AutomationName(button, b.Label);
                button.Click += async (_, _) => await RunTestAsync(b.Label);
                wrap.Children.Add(button);
            }
            ScriptKeyList.Children.Add(wrap);
        }
        ScriptKeysHint.Visibility = keys.Count + buttons.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private StackPanel KeyRow(ScriptKey k, int index)
    {
        var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
        row.Children.Add(new TextBlock { Text = k.Label.Length > 0 ? k.Label : L.T($"Key on line {k.Line}"), Style = (Style)FindResource("StackLabel") });
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        var box = new TextBox { Text = k.Key, Width = 110, IsReadOnly = true, Cursor = Cursors.Hand, ToolTip = L.T($"Click, then press the key for line {k.Line}. Esc keeps it.") };
        AutomationName(box, L.T($"Key for {(k.Label.Length > 0 ? k.Label : k.Key)}"));
        box.GotKeyboardFocus += (_, _) => box.Text = L.T("Press a key...");
        box.LostKeyboardFocus += (_, _) => box.Text = k.Key;
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Escape or Key.Tab) { Keyboard.ClearFocus(); return; }
            if (Shortcut.IsModifier(key)) return;
            int vk = KeyInterop.VirtualKeyFromKey(key);
            if (vk is <= 0 or > 0xFF) return;
            if (ScriptControls.Read(ScriptBox.Text).Keys.ElementAtOrDefault(index) is { } now)
                Apply(ScriptControls.KeyEdit(now, new KeyChord(Shortcut.CurrentModifiers(), (ushort)vk)));
            Keyboard.ClearFocus();
            RefreshScriptControls();
        };
        line.Children.Add(box);
        var pass = new CheckBox
        {
            Content = L.T("Game gets it too"), IsChecked = k.Pass, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0),
            ToolTip = L.T("Off: while the macro runs, only the script hears this key. On: the app in front gets it as well.")
        };
        void Pass(bool on)
        {
            if (ScriptControls.Read(ScriptBox.Text).Keys.ElementAtOrDefault(index) is { } now && now.Pass != on) Apply(ScriptControls.PassEdit(now, on));
            RefreshScriptControls();
        }
        pass.Checked += (_, _) => Pass(true);
        pass.Unchecked += (_, _) => Pass(false);
        line.Children.Add(pass);
        var test = new Button
        {
            Style = (Style)FindResource("IconButton"), Width = 28, Height = 28, MinHeight = 0, Margin = new Thickness(6, 0, 0, 0),
            ToolTip = L.T("Test run: plays the script's first run, then what this key does")
        };
        test.Content = new System.Windows.Shapes.Path { Data = (Geometry)FindResource("IconPlay"), Style = (Style)FindResource("AccentIcon"), Width = 10, Height = 10 };
        AutomationName(test, L.T($"Try {k.Key}"));
        test.Click += async (_, _) => await RunTestAsync(k.Key);
        line.Children.Add(test);
        row.Children.Add(line);
        if (k.Problem != null)
        {
            var problem = new TextBlock { Text = k.Problem, TextWrapping = TextWrapping.Wrap, FontSize = 12, Margin = new Thickness(0, 4, 0, 0) };
            problem.SetResourceReference(TextBlock.ForegroundProperty, k.Blocks ? "DangerTextBrush" : "MutedBrush");
            row.Children.Add(problem);
        }
        return row;
    }
    /// <summary>Makes one change in the script, so Ctrl+Z in the script undoes it.</summary>
    private void Apply(ScriptEdit edit)
    {
        if (edit.Length == 0 && edit.Text.Length == 0) return;
        ScriptBox.Document.Replace(edit.Offset, edit.Length, edit.Text);
        Say("");
    }
}
