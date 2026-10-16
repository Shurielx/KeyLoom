# Changelog

Short list of changes per version, shown in Keyloom under **About**. Full notes are on the [releases page](https://github.com/Shurielx/KeyLoom/releases).

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
