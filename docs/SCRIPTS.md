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
| `fromStart(x, y)` | A point measured from where the pointer was when the macro started. |
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
| `on(key, label?, function, { pass }?)` | Runs the function when you press the key while the macro runs. See below. |
| `button(label, function)` | A button beside the script that tries the function. See below. |

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

## Settings

A script can show some of its values beside it, as fields to change without reading the code. Mark a `const` (or `let`) with `// setting:` and a name:

```js
const SPEED = 80                  // setting: How fast
const NAME = "Bob"                // setting: Who to greet
const SELL = false                // setting: Sell the rest
const MODE = "merge"              // setting: Mode, merge | sell
const HOME = { x: 400, y: 300 }   // setting: Where the pointer rests
const GLOW = "#FFD200"            // setting: Glow, color
const BOARD = "Board"             // setting: Board, grid
const PLAY = "Play"               // setting: Play button, target
```

- A number gets a box, text in quotes a text box, and `true` or `false` a checkbox.
- Choices go after a comma, split by `|`, and show as a list.
- A point `{ x, y }` gets two boxes and **Pick**, to click the spot on the screen.
- Text with `, color` after the name gets a swatch and **Pick**, to click a color on the screen. `, grid` and `, target` show a list of this macro's grids or targets; renaming one renames it in the script too.
- Without a name after `setting:`, the field is named after the const.
- Changing a field writes the new value into the script, on that line; typing in the script changes the field. Ctrl+Z in the script undoes either.
- The mark is a comment, so the script stays plain JavaScript and runs as it is.

## Keys and buttons: a script as a small program

A script can do nothing on its own and wait for your keys, with settings and buttons beside it, so someone else can use it without reading the code. **Help** in the script starts with this guide, and **Examples** has the program below.

```js
// Set Runs to 0. Press 1 to start or pause, 2 to park the pointer.
const SPEED = 300 // setting: Pause after each merge (ms)
const BOARD = "Board" // setting: Board, grid
const HOME = { x: 400, y: 300 } // setting: Where the pointer rests

on("1", "Start or pause", () => {
  memory.working = !memory.working
  log(memory.working ? "started" : "paused")
})
on("2", "Park the pointer", () => moveTo(HOME))
button("Show the board", () => log(readGrid(BOARD).toString()))

// Nothing to do until 1 is pressed.
if (!memory.working) { wait(100); return }
const pair = readGrid(BOARD).pair()
if (pair) drag(pair.from, pair.to)
wait(SPEED)
```

**`on(key, label?, function, { pass }?)`** listens for a key while the macro runs.

- The key is named as in `press()`: `"1"`, `"F9"`, `"Ctrl+1"`. Modifiers must match, so `"Ctrl+1"` leaves a plain 1 to the game.
- The function runs at the script's next `wait()` (or while `waitFor` and `waitUntilGone` wait), or between runs. It never starts inside itself: pressing its key again while it runs waits until it ends. Other keys can still run during its `wait()`.
- Keyloom keeps the key from the app in front while the macro runs. `{ pass: true }` lets the app get it too.
- A script with keys keeps listening after its last run, until you stop it. So with **Runs** 1 the script sets up and then only reacts to keys; with **Runs** 0 it can work in a loop that keys switch on and off, as above. Keep what keys change in `memory`.
- Keys the script itself presses are not heard, and holding a key down counts once.
- The macro's own shortcut and Ctrl+Shift+F12 cannot be used: saving says so, and so does the script when it runs.
- The label names the key beside the script. There each key has a box to press a new key in, **Game gets it too** for `{ pass: true }`, and a play button that tries it. All of them change the script itself.

**`button(label, function)`** puts a button beside the script. Clicking it is a test run: the script's first run plays, then the button's function, and the macro ends. Let the top of the script return early while there is nothing to do, as above, so a button does only its own work.

## Turning steps into a script

A sequence can become a script: **Turn into script** above its steps (with Scripts on) writes a script that plays the same, every kind of step included. Groups that repeat become `for` loops, an If and its Else become `if` and `else`, grid steps use `readGrid`, and the macro keeps its targets, grids, Runs, start delay and click delay. Saving replaces the steps; until then Ctrl+Z or Cancel brings them back. A script is not turned back into steps.

## When something goes wrong

Keyloom names the line and says what it expected, for example `Line 3: press() needs a key, such as press("E") or press("Ctrl+S").` A script can catch these itself with `try` and `catch`. A misspelled name says so: `'pres' is not something Keyloom knows.`
