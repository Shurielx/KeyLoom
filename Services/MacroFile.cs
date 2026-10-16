using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using Keyloom.Engine;
using Keyloom.Models;

namespace Keyloom.Services;

/// <summary>
/// The .keyloom file: macros as plain JSON that people and AI tools can read and write. docs/FORMAT.md describes every field.
/// Reading is strict and every problem names the macro and step, so a hand-written file says what to fix.
/// </summary>
public static class MacroFile
{
    /// <summary>The format this build writes. Files with a higher number come from a newer Keyloom.</summary>
    public const int Version = 1;
    public const string Extension = ".keyloom";

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static string Write(IEnumerable<MacroProfile> macros) =>
        JsonSerializer.Serialize(new FileDto { Keyloom = Version, Macros = macros.Select(ToDto).ToList() }, Options);

    /// <summary>Reads and checks a whole file. Problems throw <see cref="JsonException"/> with a message meant for people.</summary>
    public static List<MacroProfile> Read(string json)
    {
        FileDto file;
        try { file = JsonSerializer.Deserialize<FileDto>(json, Options) ?? throw new JsonException(L.T("The file is empty.")); }
        catch (JsonException ex) { throw new JsonException(Friendly(ex), ex); }
        if (file.Keyloom == 0) throw new JsonException(L.T("This is not a Keyloom macro file. It needs \"keyloom\": 1 at the top."));
        if (file.Keyloom > Version) throw new JsonException(L.T($"This file was made by a newer Keyloom (format {file.Keyloom}). Update Keyloom to open it."));
        if (file.Keyloom < 0) throw new JsonException(L.T("The \"keyloom\" format number is invalid."));
        var macros = new List<MacroProfile>();
        var ids = new HashSet<string>();
        for (int i = 0; i < (file.Macros?.Count ?? 0); i++)
        {
            var dto = file.Macros![i] ?? throw new JsonException(L.T($"Macro {i + 1} is empty."));
            string label = string.IsNullOrWhiteSpace(dto.Name) ? $"Macro {i + 1}" : L.T($"Macro \"{dto.Name}\"");
            try
            {
                var p = FromDto(dto);
                if (string.IsNullOrWhiteSpace(p.Id) || !ids.Add(p.Id)) { p.Id = Guid.NewGuid().ToString("N"); ids.Add(p.Id); }
                ProfileValidator.Validate(p);
                macros.Add(p);
            }
            catch (ArgumentException ex) { throw new JsonException($"{label}: {ex.Message}", ex); }
        }
        return macros;
    }

    /// <summary>System.Text.Json names the path and the position; keep that, drop the type names people can't act on.</summary>
    private static string Friendly(JsonException ex)
    {
        string where = ex.Path is { Length: > 1 } path
            ? " " + (ex.LineNumber is { } line ? L.T($"(at {path.TrimStart('$', '.')}, line {line + 1})") : L.T($"(at {path.TrimStart('$', '.')})")) : "";
        string message = ex.Message;
        if (message.Contains("could not be mapped", StringComparison.Ordinal))
        {
            int start = message.IndexOf('\''), end = start < 0 ? -1 : message.IndexOf('\'', start + 1);
            string name = start >= 0 && end > start ? message[(start + 1)..end] : L.T("a field");
            return L.T($"Unknown field \"{name}\"{where}. Check its spelling against docs/FORMAT.md.");
        }
        if (message.Contains("could not be converted", StringComparison.Ordinal)) return L.T($"A value has the wrong type{where}. Numbers go without quotes, text goes in quotes.");
        return L.T($"The file is not valid JSON{where}.");
    }

    private static MacroDto ToDto(MacroProfile p)
    {
        var d = new MacroDto
        {
            Id = p.Id,
            Name = p.Name,
            Shortcut = p.PrimaryVk == 0 ? null : Shortcut.ToText(p.PrimaryModifiers, p.PrimaryVk),
            Type = p.Behavior switch { MacroBehaviorType.ClickSpam => "repeat", MacroBehaviorType.PressAndHold => "hold", MacroBehaviorType.Script => "script", _ => "sequence" },
            StartDelayMs = p.StartDelayMs
        };
        if (p.Behavior == MacroBehaviorType.Script) d.Script = p.Script.Replace("\r\n", "\n").Split('\n').ToList();
        if (!p.LooksAtScreen)
        {
            d.Press = p.TargetType == TargetInputType.Mouse ? new PressDto { Mouse = ButtonText(p.TargetMouseButton) } : new PressDto { Key = p.TargetKey };
            if (p.UsePoint && p.TargetType == TargetInputType.Mouse) d.At = new PointDto { X = p.PointX, Y = p.PointY };
        }
        if (p.Behavior == MacroBehaviorType.ClickSpam) d.EveryMs = p.SpamIntervalMs;
        if (p.Behavior != MacroBehaviorType.PressAndHold) d.Runs = p.RepeatCount;
        if (p.Targets.Count > 0) d.Targets = p.Targets.Select(t => new TargetDto
        {
            Name = t.Name,
            Picture = t.Kind == ScreenTargetKind.Picture ? t.Picture : null,
            Color = t.Kind == ScreenTargetKind.Color ? t.Color : null,
            Tolerance = t.Tolerance == ScreenTarget.DefaultTolerance ? null : t.Tolerance,
            Area = t.Area is { } a ? new AreaDto { X = a.X, Y = a.Y, Width = a.Width, Height = a.Height, From = a.Space == CoordinateSpace.Window ? "window" : null } : null
        }).ToList();
        if (p.Grids.Count > 0) d.Grids = p.Grids.Select(g => new GridDto
        {
            Name = g.Name, Rows = g.Rows, Columns = g.Columns,
            Area = new AreaDto { X = g.Area.X, Y = g.Area.Y, Width = g.Area.Width, Height = g.Area.Height, From = g.Area.Space == CoordinateSpace.Window ? "window" : null }
        }).ToList();
        if (p.Actions.Count > 0) d.Steps = ToSteps(p.Actions);
        if (p.PointerSettleMs != MacroProfile.DefaultPointerSettleMs) d.ClickDelayAfterMoveMs = p.PointerSettleMs;
        if (p.ExecutionMode != ExecutionMode.Standard) d.Input = p.ExecutionMode == ExecutionMode.Background ? "background" : "quickSwitch";
        if (!p.RestoreCursor) d.RestoreCursor = false;
        if (p.ScaleWithWindow) d.ScaleWithWindow = true;
        if (p.Window is { } w) d.Window = new WindowDto { Process = w.ProcessName, Class = NullIfEmpty(w.ClassName), Title = NullIfEmpty(w.Title), Width = w.ClientWidth, Height = w.ClientHeight };
        if (p.IconKind != MacroIconKind.Auto) d.Icon = new IconDto { Kind = Camel(p.IconKind.ToString()), Glyph = NullIfEmpty(p.IconGlyph), Image = NullIfEmpty(p.IconImage) };
        return d;
    }

    private static MacroProfile FromDto(MacroDto d)
    {
        var p = new MacroProfile { Id = d.Id ?? "", Name = d.Name?.Trim() ?? "" };
        if (p.Name.Length == 0) throw new ArgumentException(L.T("Give the macro a \"name\"."));
        (p.PrimaryModifiers, p.PrimaryVk) = Shortcut.Parse(d.Shortcut);
        p.Behavior = (d.Type ?? "").ToLowerInvariant() switch
        {
            "repeat" => MacroBehaviorType.ClickSpam,
            "hold" => MacroBehaviorType.PressAndHold,
            "sequence" => MacroBehaviorType.Sequence,
            "script" => MacroBehaviorType.Script,
            "" => throw new ArgumentException(L.T("Set \"type\" to \"repeat\", \"hold\", \"sequence\" or \"script\".")),
            _ => throw new ArgumentException(L.T($"Unknown type \"{d.Type}\". Use \"repeat\", \"hold\", \"sequence\" or \"script\"."))
        };
        if (d.Press is { } press)
        {
            if (press.Mouse != null && press.Key != null) throw new ArgumentException(L.T("\"press\" takes either \"mouse\" or \"key\", not both."));
            if (press.Key != null) { p.TargetType = TargetInputType.Keyboard; p.TargetKey = press.Key; }
            else { p.TargetType = TargetInputType.Mouse; p.TargetMouseButton = Button(press.Mouse); }
        }
        else if (!p.LooksAtScreen) throw new ArgumentException(L.T("Add \"press\", such as { \"mouse\": \"left\" } or { \"key\": \"Space\" }."));
        if (d.Script != null)
        {
            if (p.Behavior != MacroBehaviorType.Script) throw new ArgumentException(L.T("Only a macro with \"type\": \"script\" has a \"script\"."));
            p.Script = string.Join("\n", d.Script.Select(line => line ?? ""));
        }
        if (d.At is { } at) { p.UsePoint = true; p.PointX = at.X; p.PointY = at.Y; }
        if (d.EveryMs is { } every) p.SpamIntervalMs = every;
        if (d.StartDelayMs is { } start) p.StartDelayMs = start;
        if (d.Runs is { } runs) p.RepeatCount = runs;
        if (d.ClickDelayAfterMoveMs is { } settle) p.PointerSettleMs = settle;
        p.ExecutionMode = (d.Input ?? "standard").ToLowerInvariant() switch
        {
            "standard" => ExecutionMode.Standard,
            "background" => ExecutionMode.Background,
            "quickswitch" => ExecutionMode.QuickSwitch,
            _ => throw new ArgumentException(L.T($"Unknown input \"{d.Input}\". Use \"standard\", \"background\" or \"quickSwitch\"."))
        };
        if (d.RestoreCursor is { } restore) p.RestoreCursor = restore;
        if (d.ScaleWithWindow is { } scale) p.ScaleWithWindow = scale;
        if (d.Window is { } w)
        {
            if (string.IsNullOrWhiteSpace(w.Process)) throw new ArgumentException(L.T("\"window\" needs a \"process\", such as \"chrome\"."));
            p.Window = new WindowTarget { ProcessName = w.Process, ClassName = w.Class ?? "", Title = w.Title ?? "", ClientWidth = w.Width ?? 0, ClientHeight = w.Height ?? 0 };
        }
        if (d.Icon is { } icon)
        {
            if (!Enum.TryParse<MacroIconKind>(icon.Kind ?? "auto", true, out var kind) || !Enum.IsDefined(kind))
                throw new ArgumentException(L.T($"Unknown icon kind \"{icon.Kind}\". Use auto, mouse, keyboard, cursor, glyph or image."));
            p.IconKind = kind; p.IconGlyph = icon.Glyph ?? ""; p.IconImage = icon.Image ?? "";
        }
        for (int i = 0; i < (d.Targets?.Count ?? 0); i++)
        {
            var t = d.Targets![i] ?? throw new ArgumentException(L.T($"Target {i + 1} is empty."));
            string label = string.IsNullOrWhiteSpace(t.Name) ? $"Target {i + 1}" : L.T($"Target \"{t.Name}\"");
            if ((t.Picture == null) == (t.Color == null)) throw new ArgumentException(L.T($"{label}: give it either a \"picture\" or a \"color\", like \"#FF8800\"."));
            ScreenArea? area = null;
            if (t.Area is { } a)
                area = new(a.X, a.Y, a.Width, a.Height, (a.From ?? "screen").ToLowerInvariant() switch
                {
                    "screen" => CoordinateSpace.Screen,
                    "window" => CoordinateSpace.Window,
                    _ => throw new ArgumentException(L.T($"{label}: unknown area \"from\" \"{a.From}\". Use \"screen\" or \"window\"."))
                });
            p.Targets.Add(new ScreenTarget
            {
                Name = t.Name?.Trim() ?? "", Kind = t.Picture != null ? ScreenTargetKind.Picture : ScreenTargetKind.Color,
                Picture = t.Picture ?? "", Color = t.Color ?? "#000000", Tolerance = t.Tolerance ?? ScreenTarget.DefaultTolerance, Area = area
            });
        }
        for (int i = 0; i < (d.Grids?.Count ?? 0); i++)
        {
            var g = d.Grids![i] ?? throw new ArgumentException(L.T($"Grid {i + 1} is empty."));
            string label = string.IsNullOrWhiteSpace(g.Name) ? $"Grid {i + 1}" : L.T($"Grid \"{g.Name}\"");
            var a = g.Area ?? throw new ArgumentException(L.T($"{label}: it needs an \"area\", such as {{ \"x\": 100, \"y\": 200, \"width\": 400, \"height\": 400 }}."));
            p.Grids.Add(new ScreenGrid
            {
                Name = g.Name?.Trim() ?? "", Rows = Need(g.Rows, "rows", "4"), Columns = Need(g.Columns, "columns", "4"),
                Area = new(a.X, a.Y, a.Width, a.Height, (a.From ?? "screen").ToLowerInvariant() switch
                {
                    "screen" => CoordinateSpace.Screen,
                    "window" => CoordinateSpace.Window,
                    _ => throw new ArgumentException(L.T($"{label}: unknown area \"from\" \"{a.From}\". Use \"screen\" or \"window\"."))
                })
            });
        }
        if (d.Steps != null)
        {
            var path = new List<int>();
            AddSteps(p.Actions, d.Steps, path);
        }
        return p;
    }

    private static List<StepDto> ToSteps(IReadOnlyList<MacroAction> actions)
    {
        var root = new List<StepDto>();
        var open = new Stack<List<StepDto>>();
        var current = root;
        foreach (var a in actions)
        {
            if (a is { ActionType: MacroActionType.GroupStart, Block: BlockKind.If })
            {
                var test = new StepDto
                {
                    Do = "if", Target = a.AsksAi ? null : a.Target, Ask = a.AsksAi ? a.Question : null,
                    When = a.AsksAi ? (a.Gone ? "no" : null) : a.Gone ? "gone" : "shows", TimeoutMs = a.TimeoutMs > 0 && !a.AsksAi ? a.TimeoutMs : null,
                    Name = NullIfEmpty(a.Name), Collapsed = a.Collapsed ? true : null, Steps = new()
                };
                current.Add(test); open.Push(current); current = test.Steps;
                continue;
            }
            // An Else follows its If in the list; in the file it is the If's "else".
            if (a is { ActionType: MacroActionType.GroupStart, Block: BlockKind.Else } && current.LastOrDefault() is { Do: "if", Else: null } owner)
            {
                owner.Else = new();
                open.Push(current); current = owner.Else;
                continue;
            }
            if (a.ActionType == MacroActionType.GroupStart)
            {
                var group = new StepDto
                {
                    Do = "group", Name = NullIfEmpty(a.Name), Repeat = a.Repeat > 1 ? a.Repeat : null,
                    GapMs = a.GapMs > 0 ? a.GapMs : null, Collapsed = a.Collapsed ? true : null, Steps = new()
                };
                current.Add(group); open.Push(current); current = group.Steps;
                continue;
            }
            if (a.ActionType == MacroActionType.GroupEnd) { if (open.Count > 0) current = open.Pop(); continue; }
            current.Add(a.ActionType switch
            {
                MacroActionType.MouseClick => new StepDto { Do = "click", Button = ButtonText(a.MouseButton) },
                MacroActionType.MouseDown => new StepDto { Do = "hold", Button = ButtonText(a.MouseButton) },
                MacroActionType.MouseUp => new StepDto { Do = "release", Button = ButtonText(a.MouseButton) },
                MacroActionType.KeyPress => new StepDto { Do = "press", Key = a.KeyName },
                MacroActionType.KeyDown => new StepDto { Do = "hold", Key = a.KeyName },
                MacroActionType.KeyUp => new StepDto { Do = "release", Key = a.KeyName },
                MacroActionType.Delay => new StepDto { Do = "wait", Ms = a.DelayMs },
                MacroActionType.MouseMove => new StepDto { Do = "moveBy", X = a.X, Y = a.Y },
                MacroActionType.MousePosition => new StepDto
                {
                    Do = "moveTo", X = a.X, Y = a.Y,
                    From = a.Space == CoordinateSpace.Screen ? null : Camel(a.Space.ToString()),
                    GlideMs = a.GlideMs > 0 ? a.GlideMs : null
                },
                MacroActionType.MouseScroll => new StepDto { Do = "scroll", Notches = a.ScrollAmount },
                MacroActionType.ClickTarget => new StepDto
                {
                    Do = "clickOn", Target = a.Target, Button = a.MouseButton == "Left" ? null : ButtonText(a.MouseButton),
                    TimeoutMs = a.TimeoutMs > 0 ? a.TimeoutMs : null, IfTimeUp = a.StopIfMissing ? "stop" : null
                },
                MacroActionType.ClickCell => new StepDto
                {
                    Do = "clickCell", Grid = a.Grid, Pick = Camel(a.Pick.ToString()), Value = a.Pick == CellPick.Equals ? a.CellValue : null,
                    Button = a.MouseButton == "Left" ? null : ButtonText(a.MouseButton), IfTimeUp = a.StopIfMissing ? "stop" : null
                },
                MacroActionType.DragPair => new StepDto
                {
                    Do = "dragPair", Grid = a.Grid, GlideMs = a.GlideMs > 0 ? a.GlideMs : null,
                    Button = a.MouseButton == "Left" ? null : ButtonText(a.MouseButton), IfTimeUp = a.StopIfMissing ? "stop" : null
                },
                MacroActionType.WaitUntil => new StepDto
                {
                    Do = "waitUntil", Target = a.Target, Until = a.Gone ? "gone" : "shows",
                    TimeoutMs = a.TimeoutMs > 0 ? a.TimeoutMs : null, IfTimeUp = a.StopIfMissing ? "stop" : null
                },
                _ => new StepDto { Do = "type", Text = a.Text }
            });
        }
        return root;
    }

    private static void AddSteps(ICollection<MacroAction> into, List<StepDto> steps, List<int> path)
    {
        for (int i = 0; i < steps.Count; i++)
        {
            path.Add(i + 1);
            string where = "Step " + string.Join(".", path.Select(n => n < 0 ? $"{-n} else" : n.ToString())).Replace(" else.", " else ");
            var s = steps[i] ?? throw new ArgumentException(L.T($"{where} is empty."));
            try
            {
                switch ((s.Do ?? "").ToLowerInvariant())
                {
                    case "click": into.Add(new() { ActionType = MacroActionType.MouseClick, MouseButton = Button(s.Button) }); break;
                    case "hold" when s.Key != null: into.Add(new() { ActionType = MacroActionType.KeyDown, KeyName = s.Key }); break;
                    case "hold": into.Add(new() { ActionType = MacroActionType.MouseDown, MouseButton = Button(s.Button) }); break;
                    case "release" when s.Key != null: into.Add(new() { ActionType = MacroActionType.KeyUp, KeyName = s.Key }); break;
                    case "release": into.Add(new() { ActionType = MacroActionType.MouseUp, MouseButton = Button(s.Button) }); break;
                    case "press": into.Add(new() { ActionType = MacroActionType.KeyPress, KeyName = Need(s.Key, "key", L.T("\"Enter\" or \"Ctrl+C\"")) }); break;
                    case "wait": into.Add(new() { ActionType = MacroActionType.Delay, DelayMs = Need(s.Ms, "ms", "200") }); break;
                    case "moveby": into.Add(new() { ActionType = MacroActionType.MouseMove, X = s.X ?? 0, Y = s.Y ?? 0 }); break;
                    case "moveto":
                        into.Add(new()
                        {
                            ActionType = MacroActionType.MousePosition, X = Need(s.X, "x", "400"), Y = Need(s.Y, "y", "300"),
                            Space = (s.From ?? "screen").ToLowerInvariant() switch
                            {
                                "screen" => CoordinateSpace.Screen,
                                "window" => CoordinateSpace.Window,
                                "start" => CoordinateSpace.Start,
                                _ => throw new ArgumentException(L.T($"unknown \"from\" \"{s.From}\". Use \"screen\", \"window\" or \"start\"."))
                            },
                            GlideMs = s.GlideMs ?? 0
                        });
                        break;
                    case "scroll": into.Add(new() { ActionType = MacroActionType.MouseScroll, ScrollAmount = Need(s.Notches, "notches", L.T("3 (or -3 to scroll down)")) }); break;
                    case "type": into.Add(new() { ActionType = MacroActionType.Text, Text = Need(s.Text, "text", "\"Hello\"") }); break;
                    case "group":
                        into.Add(new() { ActionType = MacroActionType.GroupStart, Name = NullIfEmpty(s.Name), Repeat = s.Repeat ?? 0, GapMs = s.GapMs ?? 0, Collapsed = s.Collapsed ?? false });
                        AddSteps(into, s.Steps ?? new(), path);
                        into.Add(new() { ActionType = MacroActionType.GroupEnd });
                        break;
                    case "clickon":
                        into.Add(new()
                        {
                            ActionType = MacroActionType.ClickTarget, Target = Need(s.Target, "target", L.T("\"Play button\"")), MouseButton = Button(s.Button),
                            TimeoutMs = s.TimeoutMs ?? 0, StopIfMissing = IfTimeUp(s.IfTimeUp)
                        });
                        break;
                    case "waituntil":
                        into.Add(new()
                        {
                            ActionType = MacroActionType.WaitUntil, Target = Need(s.Target, "target", L.T("\"Loading screen\"")),
                            Gone = (s.Until ?? "shows").ToLowerInvariant() switch
                            {
                                "shows" => false, "gone" => true,
                                _ => throw new ArgumentException(L.T($"unknown \"until\" \"{s.Until}\". Use \"shows\" or \"gone\"."))
                            },
                            TimeoutMs = s.TimeoutMs ?? 0, StopIfMissing = IfTimeUp(s.IfTimeUp)
                        });
                        break;
                    case "clickcell":
                        into.Add(new()
                        {
                            ActionType = MacroActionType.ClickCell, Grid = Need(s.Grid, "grid", "\"Board\""), MouseButton = Button(s.Button), StopIfMissing = IfTimeUp(s.IfTimeUp),
                            Pick = (s.Pick ?? "highest").ToLowerInvariant() switch
                            {
                                "highest" => CellPick.Highest, "lowest" => CellPick.Lowest, "equals" => CellPick.Equals, "empty" => CellPick.Empty,
                                _ => throw new ArgumentException(L.T($"unknown \"pick\" \"{s.Pick}\". Use \"highest\", \"lowest\", \"equals\" or \"empty\"."))
                            },
                            CellValue = s.Value ?? 0
                        });
                        if (into.Last().Pick == CellPick.Equals && s.Value == null) throw new ArgumentException(L.T("\"pick\": \"equals\" needs a \"value\", such as 32."));
                        break;
                    case "dragpair":
                        into.Add(new() { ActionType = MacroActionType.DragPair, Grid = Need(s.Grid, "grid", "\"Board\""), MouseButton = Button(s.Button), GlideMs = s.GlideMs ?? 0, StopIfMissing = IfTimeUp(s.IfTimeUp) });
                        break;
                    case "if":
                        into.Add(new()
                        {
                            ActionType = MacroActionType.GroupStart, Block = BlockKind.If,
                            Target = s.Ask != null ? (s.Target == null ? null : throw new ArgumentException(L.T("an \"if\" takes a \"target\" or an \"ask\", not both.")))
                                : Need(s.Target, "target", L.T("\"Play button\"")),
                            Question = s.Ask != null ? (s.Ask.Trim().Length > 0 ? s.Ask.Trim() : throw new ArgumentException(L.T("\"ask\" needs a yes-or-no question."))) : null,
                            Gone = s.Ask != null
                                ? (s.When ?? "yes").ToLowerInvariant() switch
                                {
                                    "yes" => false, "no" => true,
                                    _ => throw new ArgumentException(L.T($"unknown \"when\" \"{s.When}\". An \"if\" that asks takes \"yes\" or \"no\"."))
                                }
                                : (s.When ?? "shows").ToLowerInvariant() switch
                                {
                                    "shows" => false, "gone" => true,
                                    _ => throw new ArgumentException(L.T($"unknown \"when\" \"{s.When}\". Use \"shows\" or \"gone\"."))
                                },
                            TimeoutMs = s.TimeoutMs ?? 0, Name = NullIfEmpty(s.Name), Collapsed = s.Collapsed ?? false
                        });
                        AddSteps(into, s.Steps ?? new(), path);
                        into.Add(new() { ActionType = MacroActionType.GroupEnd });
                        if (s.Else != null)
                        {
                            into.Add(new() { ActionType = MacroActionType.GroupStart, Block = BlockKind.Else });
                            path[^1] = -path[^1];
                            AddSteps(into, s.Else, path);
                            path[^1] = -path[^1];
                            into.Add(new() { ActionType = MacroActionType.GroupEnd });
                        }
                        break;
                    case "": throw new ArgumentException(L.T("it has no \"do\". Say what it does, such as \"do\": \"click\"."));
                    default: throw new ArgumentException(L.T($"unknown \"do\": \"{s.Do}\". Use click, hold, release, press, wait, moveTo, moveBy, scroll, type, clickOn, waitUntil, clickCell, dragPair, if or group."));
                }
                if (s.Steps != null && !(string.Equals(s.Do, "group", StringComparison.OrdinalIgnoreCase) || string.Equals(s.Do, "if", StringComparison.OrdinalIgnoreCase)))
                    throw new ArgumentException(L.T("only a group or an if can contain \"steps\"."));
                if (s.Else != null && !string.Equals(s.Do, "if", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException(L.T("only an if can have \"else\"."));
                if (into.LastOrDefault() is { } added && !added.IsGroupMarker) ProfileValidator.ValidateAction(added);
            }
            catch (ArgumentException ex) when (ex is not StepError)
            {
                throw new StepError($"{where}: {char.ToLowerInvariant(ex.Message[0])}{ex.Message[1..]}", ex);
            }
            path.RemoveAt(path.Count - 1);
        }
    }

    /// <summary>A step problem that already says which step it is, so outer groups don't prefix it again.</summary>
    private sealed class StepError(string message, Exception inner) : ArgumentException(message, inner);

    private static T Need<T>(T? value, string field, string example) where T : struct =>
        value ?? throw new ArgumentException(L.T($"it needs \"{field}\", such as {example}."));
    private static string Need(string? value, string field, string example) =>
        value ?? throw new ArgumentException(L.T($"it needs \"{field}\", such as {example}."));

    private static bool IfTimeUp(string? text) => (text ?? "continue").ToLowerInvariant() switch
    {
        "continue" => false, "stop" => true,
        _ => throw new ArgumentException(L.T($"unknown \"ifTimeUp\" \"{text}\". Use \"continue\" or \"stop\"."))
    };
    private static string Button(string? text) => (text ?? "left").ToLowerInvariant() switch
    {
        "left" => "Left", "right" => "Right", "middle" => "Middle", "x1" or "back" => "X1", "x2" or "forward" => "X2",
        _ => throw new ArgumentException(L.T($"unknown mouse button \"{text}\". Use left, right, middle, back or forward."))
    };
    private static string ButtonText(string button) => button switch { "X1" => "back", "X2" => "forward", _ => button.ToLowerInvariant() };
    private static string Camel(string name) => char.ToLowerInvariant(name[0]) + name[1..];
    private static string? NullIfEmpty(string? text) => string.IsNullOrEmpty(text) ? null : text;

    private sealed class FileDto
    {
        public int Keyloom { get; set; }
        public List<MacroDto>? Macros { get; set; }
    }
    private sealed class MacroDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Shortcut { get; set; }
        public string? Type { get; set; }
        public PressDto? Press { get; set; }
        public int? EveryMs { get; set; }
        public PointDto? At { get; set; }
        public int? StartDelayMs { get; set; }
        public int? Runs { get; set; }
        public List<StepDto>? Steps { get; set; }
        public List<string?>? Script { get; set; }
        public int? ClickDelayAfterMoveMs { get; set; }
        public string? Input { get; set; }
        public bool? RestoreCursor { get; set; }
        public WindowDto? Window { get; set; }
        public bool? ScaleWithWindow { get; set; }
        public IconDto? Icon { get; set; }
        public List<TargetDto>? Targets { get; set; }
        public List<GridDto>? Grids { get; set; }
    }
    private sealed class GridDto
    {
        public string? Name { get; set; }
        public AreaDto? Area { get; set; }
        public int? Rows { get; set; }
        public int? Columns { get; set; }
    }
    private sealed class TargetDto
    {
        public string? Name { get; set; }
        public string? Picture { get; set; }
        public string? Color { get; set; }
        public int? Tolerance { get; set; }
        public AreaDto? Area { get; set; }
    }
    private sealed class AreaDto
    {
        public int X { get; set; }
        public int Y { get; set; }
        public int Width { get; set; }
        public int Height { get; set; }
        public string? From { get; set; }
    }
    private sealed class PressDto
    {
        public string? Mouse { get; set; }
        public string? Key { get; set; }
    }
    private sealed class PointDto
    {
        public int X { get; set; }
        public int Y { get; set; }
    }
    private sealed class WindowDto
    {
        public string? Process { get; set; }
        public string? Class { get; set; }
        public string? Title { get; set; }
        public int? Width { get; set; }
        public int? Height { get; set; }
    }
    private sealed class IconDto
    {
        public string? Kind { get; set; }
        public string? Glyph { get; set; }
        public string? Image { get; set; }
    }
    private sealed class StepDto
    {
        public string? Do { get; set; }
        public string? Button { get; set; }
        public string? Key { get; set; }
        public int? Ms { get; set; }
        public int? X { get; set; }
        public int? Y { get; set; }
        public string? From { get; set; }
        public int? GlideMs { get; set; }
        public int? Notches { get; set; }
        public string? Text { get; set; }
        public string? Name { get; set; }
        public int? Repeat { get; set; }
        public int? GapMs { get; set; }
        public bool? Collapsed { get; set; }
        public string? Target { get; set; }
        public string? Until { get; set; }
        public string? When { get; set; }
        public string? Ask { get; set; }
        public string? Grid { get; set; }
        public string? Pick { get; set; }
        public long? Value { get; set; }
        public int? TimeoutMs { get; set; }
        public string? IfTimeUp { get; set; }
        public List<StepDto>? Steps { get; set; }
        public List<StepDto>? Else { get; set; }
    }
}
