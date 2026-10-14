# Keyloom

Simple keyboard and mouse macros for Windows. **0.1-dev-2**, with a modern Aero interface: Windows 11 acrylic glass, soft depth, smooth animations, and light and dark themes. Everything happens in one window: the editor, settings and help open as pages, and dialogs appear inside it.

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

## Background mode (experimental)

Turn on **Settings → Experimental features** to get **Run mode** under **More options** in the editor. *Background* sends clicks and keys straight to the target window as window messages instead of through the system input stream, so your own mouse and keyboard stay free. Use **Test background input** first: many games (including ones that read raw mouse input) ignore these messages, and Keyloom does not fall back to taking over your pointer. Running apps in an isolated virtual machine is not included; see [validation notes](docs/VALIDATION.md).

## Record

Click **Record** in the macro editor, choose what to capture, then switch to your target window during the 3-second countdown. **F8** or the recording panel stops capture; the panel also supports pause/resume.

Keep recorded delays or add your own with **Add step → Wait**. Select a step to edit it below the list; changes apply as you type, and invalid input is flagged instead of applied. Hover a step to move, duplicate or remove it (**Delete** and **Alt + ↑/↓** work too). New recordings default to one run.

Pointer positions can be recorded on the **screen**, **relative to the window** that is in front when recording starts, or **relative to the start point** (offsets from wherever the pointer is when the macro starts). **Simplify mouse path** drops samples on straight stretches while keeping clicks, holds and total timing exact.

Capture is active only while recording. Keyloom's own controls and synthetic input are excluded. Recordings stay local and are saved only with the macro. Sequences are limited to 20,000 steps; recording stops after 10 active minutes or at the step limit.

## Build

Requires Windows and the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0).

For local use, double-click **Start Keyloom.bat**. It builds the current source and opens the app, so you do not need to download release builds. Close a running copy from its tray menu before rebuilding.

```powershell
dotnet build Keyloom.csproj -c Release
dotnet run --project tests/Keyloom.Tests.csproj -c Release
dotnet run --project Keyloom.csproj
```

Alpha builds are unsigned. Some apps reject synthetic input, and game rules may restrict automation. See [validation notes](docs/VALIDATION.md) for what was tested.
