# CupkekGames Recorder

Editor-only one-call play-mode capture built on Unity Recorder. Call a `Record*` method from EDIT mode: it schedules play mode, starts recording once play is up, auto-stops at the frame count, and exits play mode itself. The session survives the play-mode domain reload; all scene mutations are play-mode-only, so there is nothing to restore.

## API (`CupkekGames.Recording.RecorderTool`)

```csharp
// Plain capture of the scene's own MainCamera.
RecorderTool.RecordMovie(durationSec, outputFile, width = 1920, height = 1080, fps = 30);

// Capture the Game View as displayed - includes UI Toolkit overlays.
RecorderTool.RecordGameView(durationSec, outputFile, width = 1920, height = 1080, fps = 30);

// Dedicated orbiting sky camera at (0,30,0) fov 62 + world-hour sweep.
RecorderTool.RecordSkyOrbit(durationSec, startHour, endHour,
    yawStart, yawSweep, pitchStart, pitchEnd, outputFile, ...);

// Same orbit with camHeight/fov exposed (ground subjects).
RecorderTool.RecordEnvironmentOrbit(durationSec, startHour, endHour,
    camHeight, fov, yawStart, yawSweep, pitchStart, pitchEnd, outputFile, ...);

// Stills sheet: hours x yaws cross product from a clone of the scene MainCamera.
RecorderTool.CaptureSkySheet(hoursCsv, yawsCsv, outPrefix, pitch = -8f, ...);
```

- `outputFile` is project-relative WITHOUT extension; the tool appends `.mp4`/`_*.png` and deletes any pre-existing output first, so "the file exists" always means "this run finished".
- All logs carry the `[CkgRecorder]` tag; watch for `[CkgRecorder] DONE` / `FAILED`.

## World-clock seam

Hour sweeps (`startHour >= 0`) require the consuming project to register its time-of-day system once per domain load:

```csharp
[InitializeOnLoadMethod]
static void Register() => RecorderTool.WorldClock = new MyWorldClock(); // IRecorderWorldClock
```

No registration + an hour sweep = loud failure. `RecordMovie`/`RecordGameView` never need it.
