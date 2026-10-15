# Keyloom

Simple keyboard and mouse macros for Windows. **0.1-dev-4**, with a modern Aero interface: Windows 11 acrylic glass, soft depth, smooth animations, and light and dark themes. Everything happens in one window: the editor, settings and help open as pages, and dialogs appear inside it.

![Keyloom](docs/screenshot.png)

## Use

Download the Windows x64 ZIP from [Releases](https://github.com/Shurielx/KeyLoom/releases), extract it, and run `Keyloom.exe`. No .NET installation, administrator access, or special keyboard is required.

1. Click **New macro**, name it, and choose **Repeat**, **Hold**, or **Sequence**.
2. Set your input, timing, and optional shortcut. Less common settings sit under **More options**.
3. Press **Start** or use the shortcut. Press it again to stop.

Sequences support clicks, key combinations, key down/up, delays, relative movement, pointer positions, scroll, and Unicode text. Use **1 run** to execute once or **0** to loop. One macro runs at a time.

- **Ctrl + Shift + F12** — stop all macros.
- **Ctrl + Shift + I** — show or hide Keyloom.
- Closing the window keeps it in the tray; use the tray menu to exit.

Profiles are stored in `%APPDATA%\Keyloom`, with a backup on save. Existing KeychronHUD profiles are imported automatically.

## Icons and themes

Click the icon next to a macro's name to choose how it looks in the list: automatic (follows the macro type), mouse, keyboard, a moving pointer, one or two characters such as **K**, **2** or **P1**, or your own picture. Pictures are cropped to a square and copied into `%APPDATA%\Keyloom\icons`, so moving or deleting the original file does not matter.

**Settings → Theme** switches between **System**, **Light** and **Dark** right away. System follows the Windows app mode.

## Pick a point

**Pick point** in the macro editor lets you click once inside another window. Keyloom remembers that window and the point relative to its content, so clicks follow the window when it moves. Choose whether positions keep their pixel distance or **scale** when the window is resized. The picking click is not passed to the app; Esc or right-click cancels.

- **Repeat / Hold** — use **Pick point** next to *Click at a chosen point* (or *Hold at…*) to use it instead of the current pointer.
- **Sequence** — choose **Add step → Click a point in a window…**; the point is added as a *Move pointer … in window* step plus a click.

If the target window is not open, the macro reports it instead of starting; if it closes while running, the macro stops.

## Preview

Sequences show a route thumbnail in the editor. **Preview** plays it with a moving marker, click pulses and a countdown for each wait, with a timeline, pause and 0.25×–4× speed. **Show on screen** draws the route exactly where it will run; the overlay lets your clicks through. Preview never moves the pointer or sends input, and it shows planned positions, not a guaranteed result.

## Run modes (experimental)

Turn on **Settings → Experimental features** to get **Run mode** under **More options** in the editor. Both modes below let you keep using your PC while a macro runs, and both need a target window chosen with **Pick point**. Use **Send a test input** to check that the target reacts.

- **Background** sends clicks and keys straight to the target window as window messages, so your own mouse and keyboard stay free. Many games, Roblox included, ignore these messages.
- **Quick switch** is for those games. For each input it brings the target window to the front for a moment (about 70 ms), sends real input at the chosen point, then gives focus and the pointer back to whatever you were using. It works best for inputs a second or more apart, such as staying active in a game. The target must not be minimized, but it can sit behind other windows or on another monitor. Hold macros are not available in this mode, and a key you press during the switch may reach the game.

Running apps in an isolated virtual machine is not included; see [validation notes](docs/VALIDATION.md).

## Record

Click **Record** in the macro editor, choose what to capture, then switch to your target window during the 3-second countdown. **F8** or the recording panel stops capture; the panel also supports pause/resume.

Keep recorded delays or add your own with **Add step → Wait**. Select a step to edit it below the list; changes apply as you type, and invalid input is flagged instead of applied. New recordings default to one run.

Pointer positions can be recorded on the **screen**, **relative to the window** that is in front when recording starts, or **relative to the start point** (offsets from wherever the pointer is when the macro starts). **Simplify mouse path** drops samples on straight stretches while keeping clicks, holds and total timing exact.

Capture is active only while recording. Keyloom's own controls and synthetic input are excluded. Recordings stay local and are saved only with the macro. Sequences are limited to 20,000 steps; recording stops after 10 active minutes or at the step limit.

## Edit steps

Each step shows its number, an icon and colour for its kind and the detail that matters; waits are small chips so the rhythm of a sequence is easy to see.

- **Pick several** with Ctrl+click, Shift+click, Ctrl+A or the checkbox that appears on hover. The panel below then edits them together: waits take **100** (set), **+20** / **-20** (add) or **x1.5** (scale), and mouse steps can switch button.
- **Drag** a row to move it; dragging a picked row moves the whole pick. **Alt + ↑/↓** moves from the keyboard.
- **Right-click** for insert wait before / after / between picked steps, group, duplicate several times, copy, cut, paste and delete. Hover the line between two rows for **+ Wait**.
- **Ctrl + C / X / V / D** copy, cut, paste and duplicate (also between macros), **Delete** removes, **Ctrl + Z / Y** undo and redo.

**Groups** keep related steps together. Pick steps and press **Ctrl + G**: a group has a name, can **play several times** and can add a **wait between its steps**, so a click with 100 ms between repeats is one setting rather than a column of copies. Groups nest, fold away with the arrow or a double-click, and **Ctrl + Shift + G** ungroups. Macros with groups need Keyloom 0.1-dev-4 or newer.

## Updates and changelog

Click the version in the bottom-right corner to open **About**: a short list of what changed in each version ([CHANGELOG.md](CHANGELOG.md)) and whether a newer release is out. Full notes are on the release pages. The changelog that came with your version works offline; when GitHub can be reached, Keyloom shows the newest one and checks the [releases](https://github.com/Shurielx/KeyLoom/releases). Keyloom only contacts GitHub while the About page is open, and it never downloads or installs anything by itself.

## Build

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

For local use, double-click **Start Keyloom.bat**. It builds the current source and opens the app, so you do not need to download release builds. Close a running copy from its tray menu before rebuilding.

```powershell
dotnet build Keyloom.csproj -c Release
dotnet run --project tests/Keyloom.Tests.csproj -c Release
dotnet run --project Keyloom.csproj
```

Alpha builds are unsigned. Some apps reject synthetic input, and game rules may restrict automation. See [validation notes](docs/VALIDATION.md) for what was tested.
