using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Keyloom.Models;
using Keyloom.Services;

namespace Keyloom.Views;

// Import and export of .keyloom files from the macro list.
public partial class MainWindow
{
    /// <summary>Bigger files are not macro files people made; reading them would only stall the window.</summary>
    private const long MaxImportBytes = 16 * 1024 * 1024;
    private const string FileFilter = "Keyloom macros (*.keyloom)|*.keyloom|All files (*.*)|*.*";

    private void Files_Click(object sender, RoutedEventArgs e)
    {
        ExportAllItem.IsEnabled = ExportOneItem.IsEnabled = _profiles.Count > 0;
        ExportOneItem.Items.Clear();
        foreach (var p in _profiles)
        {
            var item = new MenuItem { Header = p.Name, Tag = p };
            item.Click += ExportOne_Click;
            ExportOneItem.Items.Add(item);
        }
        FilesMenu.PlacementTarget = FilesButton;
        FilesMenu.IsOpen = true;
    }
    private void Import_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = FileFilter, Title = "Import macros" };
        if (dialog.ShowDialog(this) == true) Run(() => ImportAsync(dialog.FileName));
    }
    private void ExportAll_Click(object sender, RoutedEventArgs e) => AskExport(_profiles.ToList(), "Keyloom macros");
    private void ExportOne_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: MacroProfile p }) AskExport([p], p.Name);
    }
    private void AskExport(List<MacroProfile> macros, string name)
    {
        if (macros.Count == 0) return;
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = FileFilter, Title = "Export macros", DefaultExt = MacroFile.Extension, FileName = SafeFileName(name) + MacroFile.Extension };
        if (dialog.ShowDialog(this) == true) Export(dialog.FileName, macros);
    }
    private static string SafeFileName(string name)
    {
        var bad = Path.GetInvalidFileNameChars();
        string safe = new string(name.Select(c => bad.Contains(c) ? '-' : c).ToArray()).Trim(' ', '.');
        return safe.Length == 0 ? "Keyloom macros" : safe;
    }
    /// <summary>Writes the macros as a .keyloom file. Returns false and says why when it can't.</summary>
    internal bool Export(string path, IReadOnlyList<MacroProfile> macros)
    {
        try { File.WriteAllText(path, MacroFile.Write(macros)); StatusText.Text = ""; return true; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        { StatusText.Text = "The macros could not be exported: " + ex.Message; return false; }
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = CanImport && DroppedFile(e) != null ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }
    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (CanImport && DroppedFile(e) is { } path) { e.Handled = true; Run(() => ImportAsync(path)); }
    }
    private bool CanImport => OnList && !_dialogOpen && !Sheets.IsOpen;
    private static string? DroppedFile(DragEventArgs e) =>
        e.Data.GetData(DataFormats.FileDrop) is string[] { Length: 1 } files && files[0].EndsWith(MacroFile.Extension, StringComparison.OrdinalIgnoreCase) ? files[0] : null;

    /// <summary>Reads a .keyloom file and, once confirmed, adds its macros to the list. Nothing already there is replaced.</summary>
    internal async Task ImportAsync(string path, bool confirm = true)
    {
        List<MacroProfile> read;
        string file = Path.GetFileName(path);
        try
        {
            if (new FileInfo(path).Length > MaxImportBytes) { StatusText.Text = $"'{file}' is too big to be a Keyloom macro file."; return; }
            read = MacroFile.Read(await File.ReadAllTextAsync(path));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { StatusText.Text = $"'{file}' could not be imported. {ex.Message}"; return; }
        if (read.Count == 0) { StatusText.Text = $"'{file}' has no macros in it."; return; }
        var plan = MacroImport.Prepare(_profiles, read);
        string title = plan.Macros.Count == 1 ? "Import this macro?" : $"Import {plan.Macros.Count} macros?";
        if (confirm && !await Sheets.ConfirmAsync(title, MacroImport.Describe(plan), "Import")) return;
        await _executor.StopAsync();
        try { _storage.SaveProfiles(_profiles.Concat(plan.Macros).ToArray()); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        { StatusText.Text = "The macros could not be saved: " + ex.Message; return; }
        foreach (var p in plan.Macros) _profiles.Add(p);
        StatusText.Text = "";
        RegisterShortcuts();
    }
}
