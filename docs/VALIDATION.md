# Alpha-01 validation

Validated on Windows x64 on 2026-10-13.

- Release build: zero warnings and errors.
- The self-contained published EXE started successfully and responded as Keyloom. A short idle sample used about 144 MB working set and 16 ms CPU time over one second; this is a smoke measurement, not a benchmark.
- 62 automated checks passed: repeat counts, hold/release, sequences, chords, Unicode, input failure handling, immutable execution snapshots, cancellation, switching macros, paced execution, and throttled counters, plus recording and editor coverage.
- Profile persistence, backup recovery, empty lists, and legacy migration passed.
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
