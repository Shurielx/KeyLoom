using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Keyloom.Engine;
using Keyloom.Models;
using Keyloom.Services;
using Keyloom.Views;

internal static partial class Program
{
    /// <summary>Gives back the same numbers each time and remembers what size of picture it was shown.</summary>
    private sealed class FakeGridReader(params long?[] values) : IGridReader
    {
        public long?[] Values = values;
        public (int Width, int Height, int Rows, int Columns) Last;
        public long?[] Read(PixelImage image, int rows, int columns)
        {
            Last = (image.Width, image.Height, rows, columns);
            return Values;
        }
    }
    /// <summary>A grid drawn the way a game would: dark cells with a white number in each, or nothing.</summary>
    private static PixelImage DrawGrid(long?[] values, int rows, int columns, int cell)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(40, 44, 52)), null, new Rect(0, 0, columns * cell, rows * cell));
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < columns; c++)
                {
                    dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(70, 76, 90)), null, new Rect(c * cell + 3, r * cell + 3, cell - 6, cell - 6));
                    if (values[r * columns + c] is not { } v) continue;
                    var text = new FormattedText(v.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal), cell * 0.36, Brushes.White, 1);
                    dc.DrawText(text, new Point(c * cell + (cell - text.Width) / 2, r * cell + (cell - text.Height) / 2));
                }
        }
        var bitmap = new RenderTargetBitmap(columns * cell, rows * cell, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var pixels = new uint[bitmap.PixelWidth * bitmap.PixelHeight];
        bitmap.CopyPixels(pixels, bitmap.PixelWidth * 4, 0);
        return new(bitmap.PixelWidth, bitmap.PixelHeight, pixels);
    }

    private static async Task GridEngineTests()
    {
        Check(WindowsOcrReader.Number("32") == 32 && WindowsOcrReader.Number("1,024") == 1024 && WindowsOcrReader.Number("1.024") == 1024
            && WindowsOcrReader.Number("1.5K") == 1500 && WindowsOcrReader.Number("2M") == 2_000_000 && WindowsOcrReader.Number("1O") == 10,
            "numbers read as games write them: 1,024, 1.5K, 2M, and an O that is a zero");
        Check(WindowsOcrReader.Number("Score") == null && WindowsOcrReader.Number("") == null && WindowsOcrReader.Number("K") == null && WindowsOcrReader.Number("12a") == null,
            "words and pieces that are not numbers are left out");

        // Two rows of three: 4 2 .   /  8 2 4
        var reading = new GridReading(2, 3, [4, 2, null, 8, 2, 4], Enumerable.Range(0, 6).Select(i => (i % 3 * 10, i / 3 * 10)).ToArray());
        Check(reading.Pick(CellPick.Highest) == 3 && reading.Pick(CellPick.Lowest) == 1 && reading.Pick(CellPick.Equals, 4) == 0
            && reading.Pick(CellPick.Empty) == 2 && reading.Pick(CellPick.Equals, 16) == -1, "a cell is picked by the highest, lowest, a given number or none, first one on a tie");
        Check(reading.Pair() == (4, 1), "the lowest matching pair is dragged, the later cell onto the earlier one");
        Check(new GridReading(1, 3, [1, 2, 3], new (int, int)[3]).Pair() == null && reading.ToString() == "4  2  ·\n8  2  4", "no pair without two equal numbers, and the grid prints row by row");

        // Click a cell and Drag a matching pair read the grid on the screen and act on the middle of the cells.
        var screen = new FakeScreen(640, 400);
        var reader = new FakeGridReader(4, 2, null, 8, 2, 4);
        var driver = new FakeDriver();
        await using var executor = new MacroExecutor(driver, screen: screen, gridReader: reader);
        string failure = "";
        executor.ExecutionFailed += m => failure = m;
        var p = Profile(MacroBehaviorType.Sequence); p.RepeatCount = 1;
        p.Grids.Add(new ScreenGrid { Name = "Board", Area = new(100, 50, 300, 200), Rows = 2, Columns = 3 });
        p.Actions.Add(new() { ActionType = MacroActionType.ClickCell, Grid = "Board", Pick = CellPick.Highest, MouseButton = "Right" });
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        Check(driver.Events.SequenceEqual(["position:150,200", "MRight:down", "MRight:up"]) && reader.Last == (300, 200, 2, 3) && failure == "",
            "Click a cell clicks the middle of the cell with the highest number");
        driver.Events.Clear();
        p.Actions[0] = new() { ActionType = MacroActionType.DragPair, Grid = "board", GlideMs = 60 };
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        var events = driver.Events.ToArray();
        Check(events[0] == "position:250,200" && events[1] == "MLeft:down" && events[^2] == "position:250,100" && events[^1] == "MLeft:up" && events.Length > 4,
            "Drag a matching pair presses on the later cell, glides to the earlier one and lets go there");
        driver.Events.Clear();
        reader.Values = [1, 2, 3, null, null, null];
        p.Actions.Add(new() { ActionType = MacroActionType.KeyPress, KeyName = "E" });
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        Check(driver.Events.All(e => e.StartsWith('K')) && driver.Events.Count == 2 && failure == "", "with no pair the step is skipped and the macro goes on");
        p.Actions[0].StopIfMissing = true;
        driver.Events.Clear();
        await executor.ToggleAsync(p); await Until(() => executor.ActiveProfile == null);
        Check(driver.Events.IsEmpty && failure.Contains("no two cells with the same number"), "a step set to stop ends the macro when there is no pair");

        // The grids and their steps in the macro file.
        p.Actions = [new() { ActionType = MacroActionType.ClickCell, Grid = "Board", Pick = CellPick.Equals, CellValue = 32, StopIfMissing = true },
            new() { ActionType = MacroActionType.DragPair, Grid = "Board", GlideMs = 150, MouseButton = "Right" }];
        p.Grids[0].Area = new(10, 20, 300, 200);
        string json = MacroFile.Write([p]);
        var back = MacroFile.Read(json).Single();
        Check(json.Contains("\"do\": \"clickCell\"") && json.Contains("\"pick\": \"equals\"") && json.Contains("\"value\": 32") && json.Contains("\"do\": \"dragPair\"")
            && json.Contains("\"grids\""), "grids and their steps are written in plain words");
        Check(back.Grids.Single() is { Name: "Board", Rows: 2, Columns: 3 } g && g.Area == p.Grids[0].Area
            && back.Actions[0] is { ActionType: MacroActionType.ClickCell, Pick: CellPick.Equals, CellValue: 32, StopIfMissing: true }
            && back.Actions[1] is { ActionType: MacroActionType.DragPair, GlideMs: 150, MouseButton: "Right" }, "grids and their steps read back the same");
        Check(Fails(() => MacroFile.Read(json.Replace("\"grid\": \"Board\"", "\"grid\": \"Nope\"")), "no grid with that name"), "a step reading a grid the macro lacks is refused");
        Check(Fails(() => MacroFile.Read(json.Replace("\"pick\": \"equals\"", "\"pick\": \"middle\"")), "unknown \"pick\""), "an unknown pick says what to use");
        Check(Fails(() => MacroFile.Read(json.Replace("\"value\": 32,", "")), "needs a \"value\""), "picking a number needs the number");
        Check(Fails(() => MacroFile.Read(json.Replace("\"rows\": 2", "\"rows\": 500")), "up to 400 cells"), "a grid has a limit on its cells");
    }

    /// <summary>The real Windows text recognition on a drawn grid, small and large.</summary>
    private static void GridOcrTests()
    {
        long?[] values = [2, 16, null, 128, 4, 2, 1024, null, 8, 64, 32, 4, null, 256, 512, 16];
        foreach (int cell in new[] { 36, 90 })
        {
            var read = WindowsOcrReader.Instance.Read(DrawGrid(values, 4, 4, cell), 4, 4);
            Check(read.SequenceEqual(values), $"Windows reads every number in a 4 by 4 grid of {cell} px cells ({string.Join(" ", read.Select(v => v?.ToString() ?? "."))})");
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        WindowsOcrReader.Instance.Read(DrawGrid(Enumerable.Range(1, 64).Select(i => (long?)i).ToArray(), 8, 8, 60), 8, 8);
        Check(clock.ElapsedMilliseconds < 1500, $"an 8 by 8 grid is read quickly ({clock.ElapsedMilliseconds} ms)");
    }

    private static void GridUiTests()
    {
        var p = Profile(MacroBehaviorType.Sequence);
        p.Grids.Add(new ScreenGrid { Name = "Board", Area = new(100, 50, 300, 200), Rows = 2, Columns = 3 });
        p.Grids.Add(new ScreenGrid { Name = "Unused", Area = new(0, 0, 100, 100) });
        p.Actions.Add(new() { ActionType = MacroActionType.ClickCell, Grid = "Board", Pick = CellPick.Equals, CellValue = 8 });
        var editor = new MacroEditorView(p) { GridReader = new FakeGridReader() };
        var host = new Window { Content = editor, Width = 1100, Height = 760, Left = -20000, Top = -20000, ShowActivated = false };
        host.Show(); Pump(200);
        var list = (ListBox)editor.FindName("ActionsListBox");
        list.SelectedIndex = 0; Pump(100);
        Check(((StackPanel)editor.FindName("GridOptions")).IsVisible && ((Border)editor.FindName("GridCard")).IsVisible
            && ((TextBlock)editor.FindName("GridInfoText")).Text.Contains("300 × 200") && ((TextBox)editor.FindName("GridRowsTextBox")).Text == "2"
            && ((TextBox)editor.FindName("CellValueTextBox")) is { IsVisible: true, Text: "8" } && !((StackPanel)editor.FindName("DragTimePanel")).IsVisible,
            "a Click a cell step shows its grid, its size and the number it looks for");
        Invoke(editor, "ShowReading", new GridReading(2, 3, [4, 2, null, 8, 2, 4], new (int, int)[6]), 42L);
        Check(((TextBlock)editor.FindName("GridReadText")).Text == "4  2  ·\n8  2  4" && ((TextBlock)editor.FindName("GridReadNote")).Text.Contains("Read 5 of 6 cells in 42 ms")
            && ((TextBlock)editor.FindName("GridReadNote")).Text.Contains("row 2, column 2"), "Read it now shows the numbers it read and the pair it would drag");
        Render(editor, 1100, 760, "grid-step.png");
        ((TextBox)editor.FindName("GridRowsTextBox")).Text = "999"; Pump(50);
        Check(((ScreenGrid)editor.GetType().GetProperty("CurrentGrid", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(editor)!).Rows == 2,
            "a grid too big to read keeps its last good size");
        ((TextBox)editor.FindName("GridRowsTextBox")).Text = "4"; Pump(50);
        var name = (TextBox)editor.FindName("GridNameTextBox");
        name.Text = "Field";
        Invoke(editor, "RenameGrid");
        Check(((MacroAction)list.Items[0]).Grid == "Field", "renaming a grid renames it in the steps that read it");
        ((ComboBox)editor.FindName("StepTypeBox")).SelectedValue = MacroActionType.DragPair; Pump(50);
        Check(((StackPanel)editor.FindName("DragTimePanel")).IsVisible && !((StackPanel)editor.FindName("CellPickPanel")).IsVisible, "turning the step into a drag asks how long it takes");
        var read = (MacroProfile)editor.GetType().GetMethod("ReadForm", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.Invoke(editor, [p.Snapshot()])!;
        Check(read.Grids.Single() is { Name: "Field", Rows: 4 } && read.Actions[0] is { ActionType: MacroActionType.DragPair, Grid: "Field", GlideMs: 150 },
            "saving keeps the grids the steps read and drops the rest");
        host.Close();
    }
}
