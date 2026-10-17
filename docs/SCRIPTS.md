# Script macros (experimental)

A script macro does what a short JavaScript program says. It is for what steps cannot do well: counting, deciding by a number, reading a grid and choosing what to do with it.

Turn it on in **Settings → Features → Scripts**, then pick **Script** next to Sequence in the editor. **Help** (or F1 in the script) lists every function with an example to insert, Ctrl+Space suggests functions and the names of targets and grids, and the line under the script says the first mistake and takes you to it.

## How a script runs

- Each run plays the script from the top. **Runs** (0 = until you stop it) and **Pause between runs** work as for sequences.
- `return` ends the current run early; `stop()` ends the macro.
- `run` is the run number, from 1. `memory` is an object that keeps what you put in it from run to run.
- Ctrl+Shift+F12 stops any macro, a script stuck in a loop included.
- Scripts reach the keyboard, mouse and screen only through the functions below. They cannot open files, go online or start programs.
- **Test run** plays it once. What it writes with `log()`, and why it stopped, shows under **Output**.

```js
// Merge the lowest pair until none is left, then stop.
const board = readGrid("Board")
const pair = board.pair()
if (!pair) stop("No two cells match")
log("merging", pair.value)
drag(pair.from, pair.to)
wait(300)
```

## Functions

Points are on the screen, in pixels. Anything with an `x` and a `y` is a point: what `find()` gives back, a grid cell, `mouse()`, or `{ x: 400, y: 300 }`. Mouse buttons are `"left"` (the default), `"right"`, `"middle"`, `"back"` and `"forward"`. Keys are named as in steps: `"E"`, `"Space"`, `"F8"`, `"Ctrl+S"`.

| Function | What it does |
|---|---|
| `press(key)` | Presses and lets go of a key or combination. |
| `hold(key)`, `release(key)` | Holds a key down, and lets go of it. |
| `type(text)` | Types text into the app in front. |
| `click(button?)`, `click(x, y, button?)`, `click(point, button?)` | Clicks where the pointer is, or moves to a point first. |
| `mouseDown(...)`, `mouseUp(...)` | Holds a mouse button down and lets go, taking the same forms as `click`. |
| `moveTo(x, y, ms?)`, `moveTo(point, ms?)` | Moves the pointer there; with a time it glides. |
| `moveBy(x, y)` | Moves the pointer from where it is. |
| `drag(from, to, ms?, button?)` | Holds the button at one point, glides to the other and lets go. 150 ms unless you say. |
| `scroll(notches)` | Positive scrolls up, negative down. |
| `mouse()` | Where the pointer is, as `{ x, y }`. |
| `inWindow(x, y)` | A point inside the macro's target window, as a point on the screen. |
| `wait(ms)` | Pauses. 1000 is one second. |
| `waitFor(target, ms?)` | Waits until the target shows. `true`, or `false` when the time ran out. No time waits as long as it takes. |
| `waitUntilGone(target, ms?)` | Waits until the target is no longer on the screen. |
| `onScreen(target)` | `true` when the target is on the screen now. |
| `find(target)` | `{ x, y, width, height }` with x and y in its middle, or `null`. |
| `clickOn(target, button?)` | Clicks the middle of the target. `true` when it was there. |
| `colorAt(x, y)`, `colorAt(point)` | The color of one pixel, like `"#3FBF3F"`. |
| `readGrid(grid)` | Reads the numbers in a grid. See below. |
| `ask(question, where?)` | Asks the AI about the screen; gives its answer as text. Needs AI on in Settings. |
| `askYesNo(question, where?)` | `true` when the AI answers yes, `false` for anything else. |
| `askNumber(question, where?)` | The number the AI answers, or `null`. |
| `log(...things)` | Writes to Output. |
| `stop(message?)` | Ends the macro, showing the message if there is one. |
| `random(min, max)` | A whole number from min to max, both included. |
| `memory`, `run` | What carries over between runs, and the run number. |

Targets and grids are the ones listed beside the script; add them there with **New picture**, **New color** and **New grid**, and use them by name in quotes.

### Asking the AI

With **Settings → Features → AI** on, `ask`, `askYesNo` and `askNumber` show the AI a picture and pass on its answer. The AI only answers; it cannot click, type or change the macro. `where` says what it sees:

- nothing: the target window, or every screen when the macro has none
- a target or grid name: just that part, like `askNumber("How many coins?", "Coin bar")`
- `{ x, y, width, height }`: that area of the screen
- `false`: no picture, only the question

Each question takes a moment (often a second or more), and with OpenRouter each costs a little, so ask when it matters rather than in a tight loop.

```js
if (askYesNo("Is a reward window open?")) {
  clickOn("Claim")
}
```

### Grids

`readGrid("Board")` reads the grid once and gives back:

| | |
|---|---|
| `rows`, `columns` | Its size. |
| `at(row, column)` | One cell. Rows and columns count from 1, as they read on the screen. |
| `highest()`, `lowest()` | The cell with the highest or lowest number, or `null`. |
| `find(number)` | The first cell showing that number, or `null`. |
| `empty()` | The cells with no number. |
| `pair()` | `{ from, to, value }`: two cells with the same number, the lowest first, or `null`. |
| `cells()` | Every cell, row by row. |
| `toString()` | The grid as text, for `log()`. |

Each cell has `row`, `column`, `value` (a number or `null`), and `x` and `y` in its middle, so `click(board.highest())` and `drag(pair.from, pair.to)` work as they are.

A grid set to **Moves with** a picture is read where that picture has moved it, and its cells' `x` and `y` are where they are now. When the picture is not on the screen, `readGrid` stops the macro and says so.

## When something goes wrong

Keyloom names the line and says what it expected, for example `Line 3: press() needs a key, such as press("E") or press("Ctrl+S").` A script can catch these itself with `try` and `catch`. A misspelled name says so: `'pres' is not something Keyloom knows.`
