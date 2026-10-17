using Acornima;
using Acornima.Ast;

namespace Keyloom.Engine;

/// <summary>
/// A key the script listens for while it runs: <c>on("1", "Start", () => ...)</c>. <see cref="KeyOffset"/> and
/// <see cref="KeyLength"/> are where the key is written, quotes included. <see cref="PassOffset"/> is where the pass value
/// is, or where <c>, { pass: true }</c> goes when <see cref="PassLength"/> is 0.
/// </summary>
public sealed record ScriptKey(string Key, string Label, bool Pass, int Line, int KeyOffset, int KeyLength, char Quote, int PassOffset, int PassLength)
{
    /// <summary>Why the key cannot be used, or null when it can.</summary>
    public string? Problem { get; init; }
    /// <summary>True when the problem keeps the macro from being saved; a key used twice only warns.</summary>
    public bool Blocks { get; init; }
    public string Shape => $"key\u001f{Key}\u001f{Label}\u001f{Pass}\u001f{Problem}";
}
/// <summary>A button the script shows in the editor: <c>button("Reset", () => ...)</c>.</summary>
public sealed record ScriptButton(string Label, int Line)
{
    public string Shape => $"button\u001f{Label}";
}

/// <summary>Finds the keys and buttons a script declares, and writes a key or its pass back into the script.</summary>
public static class ScriptControls
{
    /// <summary>
    /// Every on() and button() written with its key and label in quotes, in order. A script that does not read gives
    /// nothing. <paramref name="own"/> is the macro's own shortcut, which no key may take.
    /// </summary>
    public static (IReadOnlyList<ScriptKey> Keys, IReadOnlyList<ScriptButton> Buttons) Read(string code, KeyChord? own = null)
    {
        Script script;
        try { script = new Parser(new ParserOptions { AllowReturnOutsideFunction = true }).ParseScript(code); }
        catch (ParseErrorException) { return ([], []); }
        var calls = new List<CallExpression>();
        var open = new Stack<Node>([script]);
        while (open.TryPop(out var node))
        {
            if (node is CallExpression { Callee: Identifier } call) calls.Add(call);
            foreach (var child in node.ChildNodes) open.Push(child);
        }
        var keys = new List<ScriptKey>();
        var buttons = new List<ScriptButton>();
        var seen = new HashSet<KeyChord>();
        foreach (var call in calls.OrderBy(c => c.Start))
        {
            var a = call.Arguments;
            switch (((Identifier)call.Callee).Name)
            {
                case "on" when a.Count >= 2 && a[0] is StringLiteral key:
                    string label = a[1] is StringLiteral named ? named.Value : "";
                    int optionsAt = a[1] is StringLiteral ? 3 : 2;
                    bool pass = false;
                    int passOffset = a[a.Count - 1].End, passLength = 0;
                    if (a.Count > optionsAt && a[optionsAt] is ObjectExpression options
                        && options.Properties.OfType<Property>().FirstOrDefault(p => p.Key is Identifier { Name: "pass" }) is { Value: BooleanLiteral flag })
                    {
                        pass = flag.Value; passOffset = flag.Start; passLength = flag.End - flag.Start;
                    }
                    string? problem = null;
                    bool blocks = true;
                    try
                    {
                        var chord = KeyChord.Parse(key.Value);
                        if (chord == KeyChord.Stop) problem = L.T("Ctrl+Shift+F12 stops every macro, so a script cannot use it.");
                        else if (chord == own) problem = L.T($"{key.Value} starts and stops this macro. Pick another key.");
                        else if (!seen.Add(chord)) { problem = L.T($"{key.Value} is used twice. The later one wins."); blocks = false; }
                    }
                    catch (ArgumentException ex) { problem = ex.Message; }
                    keys.Add(new(key.Value, label, pass, call.Location.Start.Line, key.Start, key.End - key.Start, code[key.Start], passOffset, passLength) { Problem = problem, Blocks = problem != null && blocks });
                    break;
                case "button" when a.Count >= 2 && a[0] is StringLiteral name && !buttons.Any(b => b.Label == name.Value):
                    buttons.Add(new(name.Value, call.Location.Start.Line));
                    break;
            }
        }
        return (keys, buttons);
    }

    /// <summary>The change that writes a new key where the key is, in its own quotes.</summary>
    public static ScriptEdit KeyEdit(ScriptKey key, KeyChord chord) => new(key.KeyOffset, key.KeyLength, $"{key.Quote}{chord}{key.Quote}");
    /// <summary>The change that says whether the app in front also gets the key, adding <c>{ pass: true }</c> when the call has none.</summary>
    public static ScriptEdit PassEdit(ScriptKey key, bool pass) => key.PassLength > 0
        ? new(key.PassOffset, key.PassLength, pass ? "true" : "false")
        : new(key.PassOffset, 0, pass ? ", { pass: true }" : "");
}

/// <summary>Replaces <see cref="Length"/> characters at <see cref="Offset"/> with <see cref="Text"/>.</summary>
public readonly record struct ScriptEdit(int Offset, int Length, string Text)
{
    public string ApplyTo(string code) => code[..Offset] + Text + code[(Offset + Length)..];
}
