using System.Globalization;
using System.Text;
using Keyloom.Models;

namespace Keyloom.Engine;

/// <summary>
/// Turns a sequence's steps into a script that plays the same input: groups become loops, an If and its Else become
/// if and else, grid steps read the grid. The macro's targets, grids and run settings stay as they are, so the script
/// uses them by name. One way only: a script is not turned back into steps.
/// </summary>
public static class SequenceScript
{
    public static string From(IReadOnlyList<MacroAction> steps)
    {
        var writer = new Writer();
        writer.Line(L.T("// Made from the steps of this macro. It plays the same."));
        if (steps.Count > 0) writer.Line("");
        writer.Nodes(Parse(steps), 0, 0);
        return writer.ToString();
    }

    private sealed record Node(MacroAction Step, List<Node>? Children);
    private static List<Node> Parse(IReadOnlyList<MacroAction> steps)
    {
        var root = new List<Node>();
        var open = new Stack<List<Node>>();
        var current = root;
        foreach (var a in steps)
        {
            if (a.ActionType == MacroActionType.GroupStart)
            {
                var group = new Node(a, []);
                current.Add(group); open.Push(current); current = group.Children!;
            }
            else if (a.ActionType == MacroActionType.GroupEnd) { if (open.Count > 0) current = open.Pop(); }
            else current.Add(new Node(a, null));
        }
        return root;
    }

    /// <summary>Whether a step in a group with a wait between steps is the first to play, which waits for nothing.</summary>
    private enum Place { First, Later, Unknown }

    private sealed class Writer
    {
        private readonly StringBuilder _text = new();
        private int _indent, _cells, _pairs, _played;
        public void Line(string line) => _text.Append(line.Length == 0 ? "" : new string(' ', _indent * 2)).Append(line).Append('\n');
        public override string ToString() => _text.ToString();
        private string Name(string stem, ref int count) => ++count == 1 ? stem : stem + count.ToString(CultureInfo.InvariantCulture);

        /// <summary>Sibling steps, with <paramref name="gapMs"/> before each one that plays after another, as groups do.</summary>
        public void Nodes(List<Node> nodes, int gapMs, int loops)
        {
            var place = Place.First;
            string played = "";
            for (int i = 0; i < nodes.Count; i++)
            {
                var node = nodes[i];
                if (node.Children == null || node.Step.Block == BlockKind.Group)
                {
                    Gap(gapMs, place, played);
                    place = Place.Later;
                    if (node.Children == null) Step(node.Step); else Group(node, loops);
                    continue;
                }
                // An Else plays only right after its If, which writes it; one anywhere else never plays.
                if (node.Step.Block == BlockKind.Else) continue;
                bool hasElse = i + 1 < nodes.Count && nodes[i + 1] is { Children: not null, Step.Block: BlockKind.Else };
                string mark = "";
                if (gapMs > 0 && !hasElse && place != Place.Later)
                {
                    // A lone If that does not play leaves the next step first; a flag remembers whether it played.
                    if (place == Place.First) { played = Name("played", ref _played); Line($"let {played} = false"); }
                    mark = played;
                }
                Block(node, gapMs, place, played, mark, loops, $"if ({Condition(node.Step)}) {{");
                if (hasElse)
                {
                    _text.Length -= 1; // else goes on the closing brace's line
                    _text.Append(" else {\n");
                    _indent++;
                    Gap(gapMs, place, played);
                    Nodes(nodes[i + 1].Children!, 0, loops);
                    _indent--;
                    Line("}");
                    i++;
                    place = Place.Later;
                }
                else if (place == Place.First && gapMs > 0) place = Place.Unknown;
            }
        }
        private void Block(Node node, int gapMs, Place place, string played, string mark, int loops, string opening)
        {
            Line(opening);
            _indent++;
            Gap(gapMs, place, played);
            if (mark.Length > 0) Line($"{mark} = true");
            Nodes(node.Children!, 0, loops);
            _indent--;
            Line("}");
        }
        private void Gap(int gapMs, Place place, string played)
        {
            if (gapMs <= 0 || place == Place.First) return;
            Line(place == Place.Later ? $"wait({gapMs})" : $"if ({played}) wait({gapMs})");
        }
        private void Group(Node node, int loops)
        {
            var g = node.Step;
            if (!string.IsNullOrWhiteSpace(g.Name)) Line("// " + g.Name.Replace("\r", " ").Replace("\n", " "));
            if (!g.Repeats) { Nodes(node.Children!, g.GapMs, loops); return; }
            string i = loops < 3 ? "ijk"[loops].ToString() : "i" + (loops + 1).ToString(CultureInfo.InvariantCulture);
            // A group that repeats for a time starts passes until its time is up, like the steps do.
            Line(g.ForMs > 0 ? $"for (let {i} = 0, {i}End = Date.now() + {g.ForMs}; {i} === 0 || Date.now() < {i}End; {i}++) {{"
                : $"for (let {i} = 0; {i} < {g.Runs}; {i}++) {{");
            _indent++;
            if (g.GapMs > 0 && node.Children!.Count > 0) Line($"if ({i} > 0) wait({g.GapMs})");
            Nodes(node.Children!, g.GapMs, loops + 1);
            _indent--;
            Line("}");
        }
        private static string Condition(MacroAction a)
        {
            if (a.AsksAi) return (a.Gone ? "!" : "") + $"askYesNo({Js(a.Question!)})";
            string target = Js(a.Target ?? "");
            if (a.TimeoutMs == 0) return (a.Gone ? "!" : "") + $"onScreen({target})";
            return a.Gone ? $"waitUntilGone({target}, {a.TimeoutMs})" : $"waitFor({target}, {a.TimeoutMs})";
        }
        private void Step(MacroAction a)
        {
            string button = a.MouseButton == "Left" ? "" : Js(ButtonName(a.MouseButton));
            string comma = button.Length > 0 ? ", " + button : "";
            switch (a.ActionType)
            {
                case MacroActionType.KeyPress: Line($"press({Js(a.KeyName)})"); break;
                case MacroActionType.KeyDown: Line($"hold({Js(a.KeyName)})"); break;
                case MacroActionType.KeyUp: Line($"release({Js(a.KeyName)})"); break;
                case MacroActionType.Text: Line($"type({Js(a.Text)})"); break;
                case MacroActionType.MouseClick: Line($"click({button})"); break;
                case MacroActionType.MouseDown: Line($"mouseDown({button})"); break;
                case MacroActionType.MouseUp: Line($"mouseUp({button})"); break;
                case MacroActionType.Delay: Line($"wait({a.DelayMs})"); break;
                case MacroActionType.MouseMove: Line($"moveBy({a.X}, {a.Y})"); break;
                case MacroActionType.MouseScroll: Line($"scroll({a.ScrollAmount})"); break;
                case MacroActionType.MousePosition:
                    string point = a.Space switch
                    {
                        CoordinateSpace.Window => $"inWindow({a.X}, {a.Y})",
                        CoordinateSpace.Start => $"fromStart({a.X}, {a.Y})",
                        _ => $"{a.X}, {a.Y}"
                    };
                    Line(a.GlideMs > 0 ? $"moveTo({point}, {a.GlideMs})" : $"moveTo({point})");
                    break;
                case MacroActionType.ClickTarget:
                {
                    string target = Js(a.Target ?? ""), missing = Js(L.T($"'{a.TargetName}' was not found on the screen, so the macro stopped."));
                    if (a.TimeoutMs == 0) Line(a.StopIfMissing ? $"if (!clickOn({target}{comma})) stop({missing})" : $"clickOn({target}{comma})");
                    else if (a.StopIfMissing) { Line($"if (!waitFor({target}, {a.TimeoutMs})) stop({missing})"); Line($"clickOn({target}{comma})"); }
                    else Line($"if (waitFor({target}, {a.TimeoutMs})) clickOn({target}{comma})");
                    break;
                }
                case MacroActionType.WaitUntil:
                {
                    string target = Js(a.Target ?? ""), call = a.Gone ? "waitUntilGone" : "waitFor";
                    if (a.TimeoutMs == 0) { Line($"{call}({target})"); break; }
                    string missing = Js(a.Gone ? L.T($"'{a.TargetName}' did not go away, so the macro stopped.") : L.T($"'{a.TargetName}' was not found on the screen, so the macro stopped."));
                    Line(a.StopIfMissing ? $"if (!{call}({target}, {a.TimeoutMs})) stop({missing})" : $"{call}({target}, {a.TimeoutMs})");
                    break;
                }
                case MacroActionType.ClickCell:
                {
                    string cell = Name("cell", ref _cells);
                    string pick = a.Pick switch
                    {
                        CellPick.Highest => "highest()", CellPick.Lowest => "lowest()",
                        CellPick.Equals => $"find({a.CellValue.ToString(CultureInfo.InvariantCulture)})", _ => "empty()[0]"
                    };
                    Line($"const {cell} = readGrid({Js(a.Grid ?? "")}).{pick}");
                    Line($"if ({cell}) click({cell}{comma})");
                    if (a.StopIfMissing) Line($"else stop({Js(L.T($"{a.GridName} has no cell with {a.PickText}, so the macro stopped."))})");
                    break;
                }
                case MacroActionType.DragPair:
                {
                    string pair = Name("pair", ref _pairs);
                    Line($"const {pair} = readGrid({Js(a.Grid ?? "")}).pair()");
                    Line($"if ({pair}) drag({pair}.from, {pair}.to, {Math.Max(a.GlideMs, 1)}{comma})");
                    if (a.StopIfMissing) Line($"else stop({Js(L.T($"{a.GridName} has no two cells with the same number, so the macro stopped."))})");
                    break;
                }
            }
        }
    }

    /// <summary>The word a script uses for a mouse button.</summary>
    private static string ButtonName(string button) => button switch
    {
        "Right" => "right", "Middle" => "middle", "X1" => "back", "X2" => "forward", _ => "left"
    };
    /// <summary>Text as a JavaScript string in double quotes.</summary>
    public static string Js(string text)
    {
        var s = new StringBuilder("\"");
        foreach (char c in text)
            s.Append(c switch
            {
                '"' => "\\\"", '\\' => "\\\\", '\n' => "\\n", '\r' => "\\r", '\t' => "\\t",
                (char)0x2028 => "\\u2028", (char)0x2029 => "\\u2029",
                _ when c < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString()
            });
        return s.Append('"').ToString();
    }
}
