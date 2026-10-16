# 0.1-dev-1 validation

Validated on Windows 11 x64 (12 logical cores, Intel Iris Xe) on 2026-10-14.

- Release build: zero warnings and errors. 104 automated checks passed (62 earlier checks plus 42 new).
- **Target window and point**: window-relative, start-relative and screen positions resolve at run time against a test window system; proportional scaling; missing target reported before running; closing the target stops the macro with a message; target, point, run mode and coordinate spaces round-trip through storage. A real top-level window is found again by process, class and title. The point-picker highlight was confirmed invisible to `WindowFromPoint`, and Esc cancels picking and removes its hooks.
- **Background mode**: real `PostMessage` delivery to a WinForms test window — left/right clicks reached the child control at the chosen client point and text reached the focused text box, while the real pointer stayed where it was. The executor sends only through the per-window driver in this mode.
- **Recording**: window/start conversion; path simplification keeps press/release positions and total timing, keeps curves within 2 px, and still samples at least every ~40 ms (a straight 51-sample drag became 18 samples).
- **Preview**: timeline positions, relative moves, delays and running step; scrubbing, real-time play, on-screen overlay that is click-through and never activated, and full cleanup on close.

Measurements (same machine; CPU as a share of all 12 cores):

| Scenario | CPU | Notes |
|---|---|---|
| App idle, before this release | 0.000% | 171 MB working set |
| App idle, this release | 0.000% | 167 MB working set — no idle cost from the new features |
| Preview of a 20,000-step route, paused | 0.56% | Opens in ~285 ms |
| Preview playing | 2.6% | ≈ 31% of one core; route is cached as a bitmap, only the marker redraws |
| Preview playing + on-screen overlay | 1.9% | Overlay covers only the route area, ~30 fps |
| After closing preview | 0.00% | Render loop detached |
| Background clicking, 1 ms interval | 1.4% | ~640 clicks/s delivered (timer resolution) |
| Background clicking, 50 ms interval | 0.09% | |

Benchmarks run with `dotnet run --project tests/Keyloom.Tests.csproj -c Release -- --bench`. They measure Keyloom only, not the target app.

**Not verified:** Roblox or any other game. Games often read mouse input through Raw Input or check the physical key state, so background messages may be ignored; Roblox's own support notes that running in a virtual machine needs GPU passthrough. An isolated VM mode was therefore **not** built: it would need its own guest OS and the game inside it, with far higher resource use, and Windows Sandbox is unavailable on Windows Home. Also unverified: multi-monitor setups with mixed DPI for picking and the on-screen overlay, exclusive-fullscreen games, and apps running as administrator (Windows blocks messages from a normal-privilege app to them).

# Alpha-01 validation

Validated on Windows x64 on 2026-10-13.

- Release build: zero warnings and errors.
- The self-contained published EXE started successfully and responded as Keyloom. A short idle sample used about 144 MB working set and 16 ms CPU time over one second; this is a smoke measurement, not a benchmark.
- 62 automated checks passed: repeat counts, hold/release, sequences, chords, Unicode, input failure handling, immutable execution snapshots, cancellation, switching macros, paced execution, and throttled counters, plus recording and editor coverage.
- Profile persistence, backup recovery and empty lists passed.
- Compiled WPF views were instantiated and rendered at default and compact sizes. Edit/delete icons remain intact when a macro runs. All editor modes and step editing passed.
- Real Windows hotkey registration, conflict reporting, reserved shortcut protection, and the native input structure layout passed.

The Aero UI revision was checked in the main window, running and stopped states, compact layout, and all three macro editor modes. It uses native WPF gradients and controls; no graphics package or background animation was added.

Recording tests cover timestamp preservation, held input cleanup, sampled pointer paths, exact click coordinates, pause boundaries, independent capture options, capacity limits, profile persistence, and playback through a recording input driver. Editor checks cover append/replace, quick delays, independent duplicate steps, and recording controls. Native keyboard/mouse hook installation and removal passed. The F8 stop callback was exercised with a controlled native event fixture.

The executor tests use a recording input driver. A separate opt-in test targets a focused WPF window with the real SendInput backend:

```powershell
dotnet run --project tests/Keyloom.Tests.csproj -c Release -- --input
```

This environment could not focus that test window, so the test aborted before sending input. Live physical-device recording and actual input delivery to other applications, game compatibility, exclusive fullscreen overlays, multiple-monitor DPI changes, and antivirus acceptance remain unverified. No game-account safety guarantee is made.

The application uses standard Windows input APIs. Scoped user-mode keyboard/mouse hooks are installed only for explicit recording sessions, including pause, and removed on stop or close. It installs no drivers and does not read or modify other process memory. See Microsoft's [low-level keyboard hook documentation](https://learn.microsoft.com/en-us/windows/win32/winmsg/lowlevelkeyboardproc).
