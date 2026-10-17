# Changelog

Short list of changes per version, shown in Keyloom under **About**. Full notes are on the [releases page](https://github.com/Shurielx/KeyLoom/releases).

## 0.3-dev-19 · 2026-10-18

### Added
- Add step, Repeat: plays the picked steps again and again
- Repeat them a number of times, or for a time like 30s
- Spam a click for 30s at your pace, with no counting
- Repeats show in the list with their icon: ×10 or for 30s
- The repeat says how often it plays in its time
- Quick steps get a 50 ms wait so the time can pass
- On screen and .keyloom files keep the time too

## 0.3-dev-18 · 2026-10-18

### Added
- Time fields take 10s, 2m, 1h or 1h 30m, not only ms
- A plain number still means ms
- Whole times show short, like 2m
- Works in Wait steps, Pause between runs and Start after
- Also in group waits, glides and how long to look
- Limits read in plain time too, like up to 1h

## 0.3-dev-17 · 2026-10-17

### Changed
- What's new shows one release at a time: 0.3, 0.2, 0.1
- Each build folds to tags like Added 5 or Fixed 2
- Click a build to open it, or Open all for the release
- New and installed builds start open

## 0.3-dev-16 · 2026-10-17

### Added
- Update from About with one click: Keyloom restarts in it
- Your macros and settings stay as they were
- Install puts Keyloom in the Start menu, ready to search
- The tray says when a newer version is out
- Settings: Tell me about updates turns the check off

## 0.3-dev-15 · 2026-10-17

### Added
- A picture is also found 80 to 120 percent of its size
- A grid that moves with a picture grows and shrinks with it
- So a board stays read right when the game camera zooms

## 0.3-dev-14 · 2026-10-17

### Added
- Scripts listen for keys while they run: on("1", ...)
- Keys can be combinations like Ctrl+1, kept from the game
- { pass: true } lets the game get the key too
- Change a key or its pass beside the script, or try it
- button("Label", ...) adds a button beside the script
- A script with keys keeps listening after its last run
- Settings: a point { x, y } with Pick on the screen
- Settings: a color with Pick, or a grid or target list
- Script Help starts with a guide to a small program

## 0.3-dev-13 · 2026-10-17

### Added
- Turn into script makes steps a script that plays the same
- Groups turn into loops, If and Else into if and else
- Targets, grids and run settings stay with the script
- Ctrl+Z or Cancel brings the steps back until you save
- Script settings: mark a const with // setting: and a name
- They show beside the script as boxes, checks or lists
- Change a setting there and the script changes with it
- Scripts: fromStart(x, y) for points from where you started

## 0.3-dev-12 · 2026-10-17

### Added
- Grids can move with a picture on the screen
- Set Moves with on a grid, in steps or in a script
- When a game shifts its board, the grid follows it

### Changed
- Grids read white numbers on glowing cells far better

## 0.3-dev-11 · 2026-10-17

### Featured
- **Find on screen:** click pictures and colors anywhere
- **If and Else** by what the screen shows
- **Grids:** read numbers, click or drag by them
- **New editor** with the picked step beside the list
- **Import and export** macros as .keyloom files
- **Scripts and AI** to try under Settings, Features

### Changed
- Every text is ready to translate; Keyloom stays English
- A new language is one file, see docs/TRANSLATING.md
- Counts like 1 step, 2 steps work in any language

## 0.3-dev-10 · 2026-10-17

### Added
- AI (experimental): macros can ask about the screen
- Ollama on this PC, or OpenRouter for Claude and Gemini
- If the AI says yes: steps play on the AI's answer
- Scripts: ask, askYesNo and askNumber
- Load models, Try it and Ask it now to check the setup
- The AI only looks and answers, it never clicks

## 0.3-dev-9 · 2026-10-17

### Added
- Scripts (experimental): write a macro in JavaScript
- Simple functions: press, click, clickOn, waitFor, drag
- readGrid reads numbers and finds the highest or a pair
- Editor with colors, suggestions, Help and Examples
- Mistakes show their line as you type
- Output shows what log() wrote on a test run

## 0.3-dev-8 · 2026-10-16

### Added
- Grids: read the number in each cell with Windows OCR
- Click a cell: highest, lowest, a given number or empty
- Drag a matching pair: two equal numbers, lowest first
- Mark a new grid and Read it now to check the numbers

## 0.3-dev-7 · 2026-10-16

### Changed
- Builder: targets show as boxes where they are on screen
- Builder: pick a target to see, rename and tune it
- Builder: drag around a picture to add it as a target
- Builder: other points fade and the picked one says Editing
- Builder: Click on, Wait until and If can be edited

## 0.3-dev-6 · 2026-10-16

### Added
- If: plays steps only when a target is, or is not, on screen
- Else: steps for when the If does not play
- Ifs fold, move and nest like groups, and save in the file

## 0.3-dev-5 · 2026-10-16

### Added
- Click on target: finds a picture or color and clicks it
- Wait until a picture or color shows up, or goes away
- New picture and New color take them from a frozen screen
- Targets have a name, a tolerance and Find it now
- Steps can give up and go on, or stop the macro

## 0.3-dev-4 · 2026-10-16

### Added
- Import macros from a .keyloom file, or drop it on the window
- Export all macros, or one, to share or keep a copy
- Quiet click sounds, with a switch in Settings to mute them

## 0.3-dev-3 · 2026-10-16

### Fixed
- Preview and Route open with their content, not only a shade
- Builder shows its points when the target window is minimized

### Changed
- More options is set apart and says what it holds
- A picked group shows what it does, its timing and its steps

## 0.3-dev-2 · 2026-10-16

### Changed
- Editor: steps use the full height, no scroll inside scroll
- The picked step and run settings sit in a panel beside them
- Narrow windows put that panel under the steps
- Less used settings live under More options
- Repeat, Hold or Sequence is a switch under the name
- Preview and Test run moved next to Save
- The window opens larger

## 0.3-dev-1 · 2026-10-16

### New
- Macros are saved in macros.keyloom, plain JSON you can edit
- docs/FORMAT.md describes the file, for you or an AI tool
- Mistakes in the file name the macro and the step

### Changed
- Macros from 0.2 move to the new file; the old one stays

## 0.2-dev-2 · 2026-10-16

### Fixed
- Clicking the Route card opens the preview every time

## 0.2-dev-1 · 2026-10-16

### New
- Macro builder (experimental): build macros on the screen
- Builder: right click adds clicks, holds, keys and waits
- Builder: a timeline per point and a list of all steps
- Cursor position can glide there instead of jumping
- Quick switch can put your cursor back after each step
- Picking a step rings its spot on the route

### Changed
- Clicks wait 20 ms after a move so games see it (settable)
- Move to is now called Cursor position

### Fixed
- Clicking beside Preview no longer reopens it
- Route shows every move, also without waits between them

## 0.1-dev-6 · 2026-10-16

### Fixed
- Ctrl+Z works again after deleting every step

## 0.1-dev-5 · 2026-10-16

### New
- Change name, icon and shortcut right in the list
- Steps show when they start, with lines between rows
- Run strip: every input of a run at its time
- Move to: pick the point again, click after or not
- Add a move with or without a click
- Test run: play a macro once for real
- Dry-run preview shows the step playing now
- Running card on screen with time and how to stop
- Usage statistics in Settings

### Changed
- Sharper, slightly larger text, also on 1080p
- Calmer running state, no blinking
- Route shows only when the pointer moves
- Ctrl + Shift + I removed

### Fixed
- Macro rows no longer jump and get cut off on hover

## 0.1-dev-4 · 2026-10-16

### New
- Clearer step list: numbers, icons, colours
- Pick several steps and edit them together
- Drag and drop steps
- Groups: repeat, wait between steps, fold
- Right-click menu and + Wait between rows
- Undo and redo (Ctrl+Z, Ctrl+Y)
- About page with changelog and update check

### Notes
- Macros with groups need 0.1-dev-4 or newer

## 0.1-dev-3 · 2026-10-14

### New
- Quick switch run mode for games like Roblox

### Fixed
- Macro type icons line up
- First macro row no longer cut off

## 0.1-dev-2 · 2026-10-14

### New
- Everything in one window
- Light, dark and system themes
- Redesigned editor
- Macro icons and a new app icon
- Help page

## 0.1-dev-1 · 2026-10-14

### New
- Pick a point in another window
- Background run mode (experimental)
- Route preview and on-screen overlay
- Record relative to screen, window or start point

## Alpha-01 · 2026-10-13

### New
- Repeat, Hold and Sequence macros
- Recording with editable timing
- Aero interface with acrylic glass
