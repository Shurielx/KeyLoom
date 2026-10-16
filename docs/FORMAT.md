# The .keyloom macro file

Keyloom keeps your macros in one plain JSON file: `%APPDATA%\Keyloom\macros.keyloom`. You can read it, edit it in any text editor, or ask an AI tool to write macros for you. Close Keyloom before editing the file by hand, or it may save over your changes.

**Writing macros with AI:** paste this whole page into the chat, then describe the macro you want. Ask for a complete `.keyloom` file, save it, and import it with the folder button on the macro list (or drop it on the Keyloom window). Imported macros are added beside yours; a shortcut you already use comes in unset, and Keyloom says so.

## A first example

```json
{
  "keyloom": 1,
  "macros": [
    {
      "name": "Copy and paste",
      "shortcut": "Ctrl+Shift+F9",
      "type": "sequence",
      "runs": 1,
      "steps": [
        { "do": "press", "key": "Ctrl+C" },
        { "do": "wait", "ms": 100 },
        { "do": "moveTo", "x": 800, "y": 400 },
        { "do": "click" },
        { "do": "press", "key": "Ctrl+V" }
      ]
    }
  ]
}
```

## Rules

- `"keyloom": 1` at the top is the format version. A file with a higher number needs a newer Keyloom.
- Names are case-insensitive: `"click"`, `"Click"` and `"CLICK"` all work. Keyloom writes them in camelCase.
- `//` comments and trailing commas are allowed.
- Unknown fields are refused, so a typo is never silently ignored. If something is wrong, Keyloom names the macro and the step, for example `Macro "Copy and paste": Step 2: it needs "ms", such as 200.`
- Times are in milliseconds (`ms`). Positions are in pixels.

## Macro fields

| Field | Required | Meaning |
|---|---|---|
| `name` | yes | Up to 80 characters. |
| `type` | yes | `"repeat"` presses one thing again and again, `"hold"` keeps it pressed, `"sequence"` plays `steps`, `"script"` runs `script`. |
| `shortcut` | no | Like `"Ctrl+Shift+F9"`. Needs Ctrl, Alt or Win with a key, or an F-key alone. Ctrl+Shift+F12 is reserved: it stops everything. |
| `press` | repeat, hold | `{ "mouse": "left" }` or `{ "key": "Space" }`. |
| `everyMs` | no | Repeat only. Time between presses, 1 to 3600000. Default 50. |
| `at` | no | Repeat and hold with the mouse: click at `{ "x": 40, "y": 50 }` inside `window` instead of where the pointer is. Needs `window`. |
| `runs` | no | Repeat, sequence and script. How many times it plays; `0` means until you stop it. Default 0. |
| `startDelayMs` | no | Wait before the first input. Default 500. |
| `steps` | sequence | The list of steps below. |
| `script` | script | JavaScript, one line per entry: `["click()", "wait(100)"]`. Experimental; the functions are in [SCRIPTS.md](SCRIPTS.md). |
| `clickDelayAfterMoveMs` | no | Wait after the pointer moves before a button is pressed, so games see the move. 0 to 1000, default 20. |
| `window` | no | The window that positions are measured in: `{ "process": "chrome", "title": "Reports" }`. Also `class`, and `width` and `height` of its inside when the point was picked. |
| `scaleWithWindow` | no | `true` scales positions when the window is resized. |
| `input` | no | `"standard"` (default) sends real input. `"background"` sends it to `window` without moving your pointer. `"quickSwitch"` brings `window` to the front for each step, then hands it back. |
| `restoreCursor` | no | Quick switch only. `false` leaves the pointer where the macro put it. |
| `targets` | no | Pictures and colors the steps find on the screen. See [Targets](#targets). |
| `grids` | no | Grids of numbers the steps read. See [Grids](#grids). |
| `icon` | no | `{ "kind": "glyph", "glyph": "R" }`. Kinds: auto, mouse, keyboard, cursor, glyph, image. |
| `id` | no | Keyloom adds one. Keep it when you edit a file so usage history stays with the macro. |

## Steps

Every step has `"do"`, which says what it does.

| `do` | Fields | Example |
|---|---|---|
| `click` | `button` (default left) | `{ "do": "click", "button": "right" }` |
| `hold` | `button` or `key` | `{ "do": "hold", "button": "left" }`, `{ "do": "hold", "key": "Shift" }` |
| `release` | `button` or `key` | `{ "do": "release", "key": "Shift" }` |
| `press` | `key` | `{ "do": "press", "key": "Ctrl+S" }` |
| `wait` | `ms` | `{ "do": "wait", "ms": 250 }` |
| `moveTo` | `x`, `y`, `from`, `glideMs` | `{ "do": "moveTo", "x": 412, "y": 260, "from": "window", "glideMs": 120 }` |
| `moveBy` | `x`, `y` | `{ "do": "moveBy", "x": 0, "y": -40 }` |
| `scroll` | `notches` | `{ "do": "scroll", "notches": -3 }` (negative scrolls down) |
| `type` | `text` | `{ "do": "type", "text": "Hello" }` |
| `clickOn` | `target`, `button`, `timeoutMs`, `ifTimeUp` | `{ "do": "clickOn", "target": "Play button", "timeoutMs": 2000 }` |
| `waitUntil` | `target`, `until`, `timeoutMs`, `ifTimeUp` | `{ "do": "waitUntil", "target": "Loading", "until": "gone" }` |
| `clickCell` | `grid`, `pick`, `value`, `button`, `ifTimeUp` | `{ "do": "clickCell", "grid": "Board", "pick": "highest" }` |
| `dragPair` | `grid`, `glideMs`, `button`, `ifTimeUp` | `{ "do": "dragPair", "grid": "Board", "glideMs": 150 }` |
| `if` | `target` or `ask`, `when`, `timeoutMs`, `steps`, `else` | see [If](#if) |
| `group` | `steps`, `repeat`, `gapMs`, `name`, `collapsed` | see below |

- **Buttons:** `left`, `right`, `middle`, `back`, `forward`.
- **Keys:** names like `A`, `1`, `F8`, `Enter`, `Space`, `Esc`, `Tab`, `Left`, `Up`, `Delete`, `LeftCtrl`, `RightShift`, or combinations like `Ctrl+Shift+S`.
- **`from`** in `moveTo`: `screen` (default) counts from the top-left of the screen, `window` from the top-left inside the macro's `window`, `start` from where the pointer was when the macro started.
- **`glideMs`** moves the pointer there over that time instead of jumping.
- A `hold` should have a matching `release` later in the steps.
- **`clickOn`** finds its target on the screen and clicks the middle of it. It keeps looking for `timeoutMs` (default 0: look once).
- **`waitUntil`** holds the macro until its target shows (`"until": "shows"`, the default) or is gone (`"until": "gone"`). `timeoutMs` 0 (the default) waits for as long as it takes.
- **`ifTimeUp`** says what happens when the time runs out: `"continue"` (default) goes on with the next step, `"stop"` stops the macro.
- **`clickCell`** reads its grid and clicks the middle of one cell. `pick` is `highest` (default), `lowest`, `equals` (the cell showing `value`) or `empty`. On a tie it takes the first cell, reading row by row.
- **`dragPair`** reads its grid, finds two cells with the same number, the lowest number first, and drags the later cell onto the earlier one over `glideMs`.
- For `clickCell` and `dragPair`, `ifTimeUp` says what happens when there is no such cell: `"continue"` (default) or `"stop"`.

### Groups

A group plays its steps `repeat` times, with `gapMs` between its steps and between its runs. Groups can hold other groups, up to 8 deep.

```json
{ "do": "group", "name": "Burst", "repeat": 5, "gapMs": 30, "steps": [
  { "do": "click" },
  { "do": "moveBy", "x": 10, "y": 0 }
] }
```

## Targets

A target is something on the screen a macro looks for: a small `picture`, or one `color`. Steps name it in `target`.

```json
"targets": [
  { "name": "Play button", "picture": "iVBORw0KGgo...", "tolerance": 10 },
  { "name": "Full health", "color": "#3FBF3F", "tolerance": 4, "area": { "x": 20, "y": 900, "width": 300, "height": 40 } }
]
```

| Field | Meaning |
|---|---|
| `name` | Up to 60 characters, unique in the macro. |
| `picture` | A PNG as base64 text, up to 600 by 600 pixels. Capture it in Keyloom with New picture; it is not something to write by hand. |
| `color` | `"#RRGGBB"`. Give either `picture` or `color`. |
| `tolerance` | 0 to 100, how different the screen may look and still count. 0 needs it exact. Default 10. |
| `area` | Where to look: `x`, `y`, `width`, `height`, and `from`: `screen` (default) or `window`. Without it, Keyloom looks on the whole screen, or in the macro's `window` when it has one. A smaller area is faster. |

A picture is found where it matches, wherever it moved to; a color is found as the first patch of it from the top, and clicked in its middle. Keyloom reads the screen, so the target has to be visible: not covered, and not on a minimized window.

## Grids

A grid is a part of the screen split into equal cells, each holding a number or nothing. Steps name it in `grid`.

```json
"grids": [
  { "name": "Board", "area": { "x": 640, "y": 300, "width": 480, "height": 480 }, "rows": 4, "columns": 4 }
]
```

| Field | Meaning |
|---|---|
| `name` | Up to 60 characters, unique in the macro. |
| `area` | The whole grid, edge to edge: `x`, `y`, `width`, `height`, and `from`: `screen` (default) or `window`. |
| `rows`, `columns` | How many cells across and down, up to 400 cells in all. |

Numbers are read with the text recognition built into Windows. `1,024`, `1.5K` and `2M` count as numbers; other text in a cell is left out.

## If

An `if` plays its `steps` only when its target is on the screen (`"when": "shows"`, the default) or not on it (`"when": "gone"`). Its `else` steps play when it does not. `timeoutMs` lets the `if` keep looking for up to that long before it decides; 0 (the default) checks once. An `if` can sit inside groups and other ifs.

```json
{ "do": "if", "target": "Reward chest", "steps": [
  { "do": "clickOn", "target": "Reward chest" },
  { "do": "wait", "ms": 500 }
], "else": [
  { "do": "press", "key": "R" }
] }
```

**Asking the AI (experimental).** Instead of a `target`, an `if` can have `ask`: a yes-or-no question the AI answers by looking at the macro's `window`, or the whole screen. Its steps play when the answer is yes (`"when": "yes"`, the default) or no (`"when": "no"`); an answer that is neither counts as no. It needs AI turned on in Settings; the AI only answers, it never clicks or types.

```json
{ "do": "if", "ask": "Is a reward window open?", "steps": [
  { "do": "clickOn", "target": "Claim" }
] }
```

## Limits

Up to 20,000 steps, 100 targets and 20 grids per macro and 1,000,000 played steps per run once groups are repeated. The whole file can be up to 8 MB.
