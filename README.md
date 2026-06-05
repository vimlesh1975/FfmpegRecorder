# FfmpegRecorder

Windows x64 WinForms recorder and playout tool for Blackmagic DeckLink workflows.

The app is built for a fixed `1920x1080` operator screen. It records four DeckLink inputs, records stream/URL sources, previews video with audio meters, and plays recorded files back to both the local preview and DeckLink SDI output.

## What It Does

- Records up to four DeckLink inputs: `CAM1`, `CAM2`, `CAM3`, and `CAM4`.
- Records direct stream URLs, local files, YouTube URLs, and Facebook / `fb.watch` URLs.
- Shows live preview and left/right audio meters.
- Supports `Record All`, `Stop All`, individual record/stop, and per-recorder opt-in.
- Saves recorder source, profile, interval, input mode, PAL aspect, audio-listen, player output, and recording-folder settings.
- Creates one dedicated folder per recorder under the selected recording root.
- Includes a DeckLink Player tab with folder tree, file grid, duration column, preview, audio meters, scrub/play controls, and DeckLink output selection.
- Uses local bundled tools beside the app executable instead of relying on system `PATH`.

## Important Folders

```text
DeckLinkOutputHelper\
```

This folder is required for reliable DeckLink Player SDI output. Do not delete it. The build copies its files into the output folder as `DeckLinkOutputHelper.exe` plus required DLL/JSON files.

```text
bin\Debug\net10.0-windows\
```

This is the normal debug output folder. After a successful build, the main app executable is timestamped, for example:

```text
FfmpegRecorder_20260605_132811.exe
```

The app should not create `decklinkplayer.exe` or `decklinkplayer_*.exe`. DeckLink Player SDI output uses `DeckLinkOutputHelper.exe`.

## Requirements

- Windows x64.
- .NET 10 Windows Desktop runtime / SDK.
- Blackmagic Desktop Video drivers.
- Blackmagic DeckLink hardware.
- A DeckLink-enabled local FFmpeg build.
- `DeckLinkOutputHelper\` folder from this repository.

Required binaries beside the built app:

```text
ffmpeg.exe
ffplay.exe
ffprobe.exe
DeckLinkOutputHelper.exe
```

Optional binaries:

```text
ffmbc.exe or ffmbc-*.exe
yt-dlp.exe
```

Use `ffmbc` for the Sony-compatible MXF profile. Use `yt-dlp.exe` for YouTube/Facebook page URLs.

## Build

```powershell
dotnet build .\FfmpegRecorder.vbproj
```

The build:

- Builds the main recorder.
- Copies `DeckLinkOutputHelper\` files into the output folder.
- Removes old timestamped `FfmpegRecorder_*.exe` files.
- Removes stale `decklinkplayer*.exe` files.
- Renames the main app exe to a fresh timestamped exe.

Expected output:

```text
bin\Debug\net10.0-windows\FfmpegRecorder_yyyyMMdd_HHmmss.exe
```

## Run

Open the latest timestamped exe:

```text
bin\Debug\net10.0-windows\FfmpegRecorder_yyyyMMdd_HHmmss.exe
```

Before recording or playout, confirm these files exist in the same output folder:

```text
ffmpeg.exe
ffplay.exe
ffprobe.exe
DeckLinkOutputHelper.exe
DeckLinkAPI.Interop.dll
decklinkplayer.dll
decklinkplayer.deps.json
decklinkplayer.runtimeconfig.json
```

## DeckLink Inputs

Default preferred input mapping:

| Camera | Preferred input |
| --- | --- |
| `CAM1` | `DeckLink SDI 4K` |
| `CAM2` | `DeckLink Duo (1)` |
| `CAM3` | `DeckLink Duo (2)` |
| `CAM4` | `DeckLink Duo (3)` |

Each camera can also be set to `None`. This is useful when only some DeckLink cards are available or when you want to free a device.

If two camera panels try to use the same DeckLink source, the app avoids silently duplicating the source and swaps/updates assignments where possible.

## Input Modes And PAL

Default input mode is:

```text
Auto
```

Available modes:

| Mode | Use |
| --- | --- |
| `Auto` | Let FFmpeg/DeckLink detect the input where supported. |
| `1080i50` | Standard HD interlaced workflow. |
| `PAL` | Force SD PAL input handling. |

PAL aspect options:

| PAL Aspect | Result |
| --- | --- |
| `4:3` | Upconverts PAL 4:3 to `1920x1080` with correct geometry/pillarbox. |
| `16:9` | Upconverts anamorphic PAL widescreen to full-frame `1920x1080`. |

Use `PAL` when you know the input is standard-definition PAL. Use `Auto` when the card/input may change between HD and PAL.

## Recording Profiles

| Profile | Extension | Notes |
| --- | --- | --- |
| `XDCAM HD422` | `.mxf` | MPEG-2 4:2:2 MXF. |
| `XDCAM Sony Compatible` | `.mxf` | Uses FFmbc finalization for Sony-friendly MXF. |
| `MP4 High Quality` | `.mp4` | H.264 high-quality file. |
| `MP4 Low Bitrate` | `.mp4` | Smaller H.264 file. |
| `ProRes Proxy (Small)` | `.mov` | Lightweight ProRes proxy. |
| `ProRes LT (Light)` | `.mov` | ProRes LT. |
| `ProRes 422 (Medium)` | `.mov` | Standard ProRes 422. |
| `ProRes 422 HQ (High)` | `.mov` | ProRes 422 HQ. |

For PAL input, the app can upconvert to HD before recording according to the selected PAL aspect.

## Sony-Compatible MXF

`XDCAM Sony Compatible` uses a temporary FFmbc workflow.

Temporary files are written under:

```text
<RecordingDirectory>\<RecorderName>\.ffmbc-temp\
```

Completed files are finalized in the background and moved into the recorder folder. Do not delete `.ffmbc-temp` while finalization is running.

## Recording Folders

Default root:

```text
C:\Users\<User>\Videos\FFmpegRecorder
```

Each recorder gets its own folder:

```text
<RecordingDirectory>\CAM1
<RecordingDirectory>\CAM2
<RecordingDirectory>\CAM3
<RecordingDirectory>\CAM4
<RecordingDirectory>\STREAM
```

The DeckLink Player tab reads from the selected recording root. Clicking the already-selected folder in the tree refreshes the file grid.

## Stream Recorder

The stream recorder accepts:

- Direct media URLs.
- Local file paths.
- YouTube page URLs.
- Facebook / `fb.watch` URLs.

For YouTube/Facebook page URLs, keep `yt-dlp.exe` beside the app executable.

Behavior:

- Live URLs record at live speed.
- Finite files/VOD URLs can record faster than real time.
- Recording stops automatically when a finite URL/file reaches end of input.
- Preview remains real-time for operator confidence.

Stream files are named like:

```text
Stream_ddMMyyyy_HHmmss.ext
```

## DeckLink Player

The DeckLink Player tab is for playout of recorded files.

It provides:

- Folder tree from the selected recording root.
- File grid with duration.
- Local preview with audio bars.
- Persistent `Listen Audio` support for player audio monitoring.
- Persistent SDI output device/mode selection.
- DeckLink output through `DeckLinkOutputHelper.exe`.

The player uses FFmpeg for decoding and the Blackmagic DeckLink SDK helper for SDI output. This is more reliable than FFmpeg's DeckLink muxer on the current hardware.

If `DeckLinkOutputHelper.exe` is missing, the app may fall back to FFmpeg DeckLink output, but SDI output may fail or be blank. Keep `DeckLinkOutputHelper\` in the repository and `DeckLinkOutputHelper.exe` in the build output.

## DeckLink Output Helper

Source/package folder:

```text
DeckLinkOutputHelper\
```

Copied to build output as:

```text
bin\Debug\net10.0-windows\DeckLinkOutputHelper.exe
```

This project is independent of `.reference`. The `.reference` folder is not required and should not be needed for normal builds.

Quick dry-run example:

```powershell
.\bin\Debug\net10.0-windows\DeckLinkOutputHelper.exe dry-run `
  --ffmpeg-path .\bin\Debug\net10.0-windows\ffmpeg.exe `
  --input "D:\video\CAM4\CAM4_05062026_131400.mxf" `
  --device "DeckLink SDI 4K" `
  --format-code Hi50 `
  --video-size 1920x1080 `
  --frame-rate 25 `
  --pixel-format uyvy422 `
  --audio-channels 2 `
  --preroll 0.5 `
  --video-filter "scale=1920:1080:force_original_aspect_ratio=decrease,pad=1920:1080:(ow-iw)/2:(oh-ih)/2,setsar=1,fps=25,setpts=N/(25*TB)" `
  --loop
```

## Settings

Settings are stored under:

```text
C:\Users\<User>\AppData\Roaming\FfmpegRecorder
```

Common files include:

```text
recording-directory.txt
audio-listen.txt
decklink-player-output.txt
settings-CAM1.txt
settings-CAM2.txt
settings-CAM3.txt
settings-CAM4.txt
```

## Troubleshooting

### No DeckLink Output In Player

Check:

- `DeckLinkOutputHelper.exe` exists beside the app exe.
- `DeckLinkAPI.Interop.dll`, `decklinkplayer.dll`, and helper `.json` files exist beside it.
- The selected `SDI Out` device matches the physical cable/output card.
- The selected mode matches the monitor/router format, usually `1080i50` / `Hi50`.
- Blackmagic Desktop Video sees the card.
- No old FFmpeg/helper process is still holding the card.

List FFmpeg DeckLink devices:

```powershell
.\bin\Debug\net10.0-windows\ffmpeg.exe -sinks decklink
.\bin\Debug\net10.0-windows\ffmpeg.exe -sources decklink
```

### Build Creates `decklinkplayer.exe`

It should not. The intended helper name is:

```text
DeckLinkOutputHelper.exe
```

If `decklinkplayer*.exe` appears in the output folder, delete it and rebuild. The project target also removes stale `decklinkplayer*.exe` files during build.

### Stop Recording Takes Time

FFmpeg needs a moment to flush, close containers, and finalize files. Sony-compatible MXF can take longer because FFmbc finalization runs after capture.

### App Closed But Processes Remain

The app attempts to kill bundled helper processes on startup, shutdown, and rebuild. If Task Manager still shows old `ffmpeg.exe` processes, confirm they are from this app's output folder before killing them.

## Project Structure

| Path | Purpose |
| --- | --- |
| `Program.vb` | Startup and bundled helper cleanup. |
| `RecorderHostForm.vb` | Main operator form, common controls, tabs, CPU/free-space display. |
| `RecorderHostForm.Designer.vb` | Fixed 1920x1080 main layout. |
| `Form1.vb` | DeckLink recorder control for each camera. |
| `RecorderOptions.vb` | FFmpeg argument generation for recording and preview. |
| `StreamRecorderControl.vb` | Stream, URL, local file recorder. |
| `DeckLinkPlayerControl.vb` | Folder tree, file grid, preview, meters, DeckLink playout. |
| `FfmpegProcessRunner.vb` | Process wrapper for FFmpeg, FFplay, helper tools. |
| `PreviewFrameReader.vb` | Pipe-based preview frame reader. |
| `NetworkPreviewReader.vb` | TCP preview reader during recording. |
| `RecordingDirectorySettings.vb` | Recording root persistence. |
| `FfmbcConversionQueue.vb` | Sony-compatible MXF finalization queue. |
| `DeckLinkOutputHelper\` | Required SDK playout helper package. |

## Repository

[https://github.com/vimlesh1975/FfmpegRecorder](https://github.com/vimlesh1975/FfmpegRecorder)
