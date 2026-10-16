# Translating Keyloom

Keyloom ships in English. Every text it shows is ready for translation, so adding a language needs no code changes: just one file.

## How it works

- The English text is the key. A language is a JSON file, `Assets/Lang/<code>.json`, that maps each English text to its translation. `<code>` is the two-letter language code, like `de` or `pl`.
- Anything missing or left empty in the file shows in English, so a partly done translation still works.
- Once Keyloom has a second language, **Settings → Appearance → Language** appears. *Same as Windows* picks the Windows language when Keyloom has it. The change shows after a restart.

## Making a language

1. Build the tests and write a template with every text in it:

   ```
   dotnet build tests/Keyloom.Tests.csproj
   dotnet tests/bin/Debug/net10.0-windows10.0.19041.0/Keyloom.Tests.dll --dump-strings Assets/Lang/de.json
   ```

   Add the code again at the end (`... --dump-strings Assets/Lang/de.json de`) to keep the translations a file already has, for example after Keyloom gained new texts.

2. Fill in the values. Keep these as they are:
   - `{0}`, `{1:N0}` and the like: Keyloom puts numbers and names there. Move them to where they belong in your sentence, but keep each one.
   - Key names (`Ctrl+Z`, `Esc`, `F8`), JSON field names in quotes (`"type"`, `"steps"`) and script function names (`press()`, `readGrid()`).
   - Spaces at the start or end of a text: it is joined to another one.

3. Texts with a count have two English forms split by `|`, like `"{0} step|{0} steps"`. Write your language's forms the same way:
   - two forms (one, more) for most languages, like German: `"{0} Schritt|{0} Schritte"`
   - three forms for Polish, Czech, Slovak, Russian and Ukrainian (1, 2 to 4, 5 and more): `"{0} krok|{0} kroki|{0} kroków"`

4. Build Keyloom. Every file in `Assets/Lang` is built in, and the new language shows up in Settings.

## For code

- Wrap text people read in `L.T(...)`: `L.T("Save macro")`, or with values `L.T($"Added {name}.")`. The key is then `Added {0}.`.
- Write whole sentences. Pick between two sentences rather than gluing pieces together, so a translation can put the words in its own order.
- Counts go through `L.N(count, "{0} step", "{0} steps")`.
- XAML needs nothing: each view calls `L.Apply(this)` after `InitializeComponent()`, which translates its texts, buttons, tool tips and screen reader names.
- Texts for the AI and the scripts' own code stay English.
