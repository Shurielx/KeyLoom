using Keyloom.Models;

namespace Keyloom.Services;

/// <summary>Imported macros made ready to sit beside the ones already there.</summary>
/// <param name="Macros">The macros to add, in file order.</param>
/// <param name="ShortcutsCleared">Names of macros whose shortcut was already taken and is now unset.</param>
public sealed record ImportPlan(List<MacroProfile> Macros, List<string> ShortcutsCleared);

public static class MacroImport
{
    /// <summary>
    /// Imported macros are always added, never replace anything: a macro whose id is already in the list gets a new one,
    /// a shortcut that is taken is unset, and a picture icon that is not on this PC falls back to the automatic icon.
    /// </summary>
    public static ImportPlan Prepare(IEnumerable<MacroProfile> existing, IEnumerable<MacroProfile> incoming, Func<string, bool>? hasImage = null)
    {
        var current = existing.ToList();
        var ids = current.Select(p => p.Id).ToHashSet();
        var taken = current.Where(p => p.PrimaryVk != 0).Select(p => (p.PrimaryModifiers, p.PrimaryVk))
            .Concat(current.Where(p => p.FallbackVk != 0).Select(p => (p.FallbackModifiers, p.FallbackVk))).ToHashSet();
        hasImage ??= name => IconStore.IsValidName(name) && IconStore.Load(name) != null;
        var macros = new List<MacroProfile>();
        var cleared = new List<string>();
        foreach (var p in incoming)
        {
            if (!ids.Add(p.Id)) { p.Id = Guid.NewGuid().ToString("N"); ids.Add(p.Id); }
            if (p.PrimaryVk != 0 && !taken.Add((p.PrimaryModifiers, p.PrimaryVk)))
            {
                p.PrimaryVk = 0; p.PrimaryModifiers = 0;
                cleared.Add(p.Name);
            }
            if (p.FallbackVk != 0 && !taken.Add((p.FallbackModifiers, p.FallbackVk))) { p.FallbackVk = 0; p.FallbackModifiers = 0; }
            if (p.IconKind == MacroIconKind.Image && !hasImage(p.IconImage)) { p.IconKind = MacroIconKind.Auto; p.IconImage = ""; }
            macros.Add(p);
        }
        return new(macros, cleared);
    }

    /// <summary>One line for the confirmation: how many, and what changed on the way in.</summary>
    public static string Describe(ImportPlan plan)
    {
        int n = plan.Macros.Count;
        string text = n == 1 ? $"'{plan.Macros[0].Name}' is added to your macros." : $"{n} macros are added to your list. Nothing you already have is replaced.";
        if (plan.ShortcutsCleared.Count > 0)
            text += plan.ShortcutsCleared.Count == 1
                ? $" '{plan.ShortcutsCleared[0]}' comes without a shortcut, because yours already use it."
                : $" {plan.ShortcutsCleared.Count} come without a shortcut, because yours already use them: {string.Join(", ", plan.ShortcutsCleared.Select(x => $"'{x}'"))}.";
        return text;
    }
}
