# FfmpegRecorder

Windows desktop recorder for Blackmagic DeckLink inputs and network/file streams, built with VB.NET WinForms and local FFmpeg tools.

The app is intended for live operators who need quick visual confirmation, per-camera or global record control, selectable audio monitoring, and timestamped segmented clips.

## Features

- Records up to 4 DeckLink camera inputs: `CAM1`, `CAM2`, `CAM3`, and `CAM4`
- Records stream, URL, or local file sources through a separate stream recorder panel
- Shows live preview with left/right audio level meters
- Supports individual record/stop and shared `Record All` / `Stop All`
- Lets each camera opt in or out of `Record All`
- Monitors one selected camera audio feed at a time
- Shows per-camera CPU usage and total PC CPU usage
- Provides a configurable recording directory with a browse button
- Supports dark mode
- Saves camera device, profile, interval, and recording-folder settings between runs
- Uses local media binaries beside the app executable instead of falling back to `PATH`

## Default Camera Mapping

The app prefers these DeckLink inputs by default:

| Camera | Preferred DeckLink input |
| --- | --- |
| `CAM1` | `DeckLink SDI 4K` |
| `CAM2` | `DeckLink Duo (1)` |
| `CAM3` | `DeckLink Duo (2)` |
| `CAM4` | `DeckLink Duo (3)` |

The app reserves DeckLink inputs while panels are active so two camera panels do not silently use the same device.

## Recording Profiles

| Profile | Extension | Notes |
| --- | --- | --- |
| `XDCAM HD422` | `.mxf` | MPEG-2 4:2:2 broadcast-style MXF |
| `XDCAM Sony Compatible` | `.mxf` | Sony-compatible MXF workflow finalized with FFmbc |
| `MP4 High Quality` | `.mp4` | H.264, CRF 18, AAC audio |
| `MP4 Low Bitrate` | `.mp4` | H.264, CRF 24, AAC audio |
| `ProRes Proxy (Small)` | `.mov` | ProRes proxy |
| `ProRes LT (Light)` | `.mov` | Lightweight ProRes |
| `ProRes 422 (Medium)` | `.mov` | Balanced ProRes |
| `ProRes 422 HQ (High)` | `.mov` | Highest quality ProRes profile in this app |

DeckLink camera recorders now support three input modes in the operator UI. The default is `Auto`:

- `1080i50`: existing HD workflow
- `PAL`: explicit SD PAL capture
- `Auto`: lets FFmpeg/DeckLink auto-detect the incoming video mode when supported by the hardware

When `PAL` or `Auto` is used, you can also choose `PAL Aspect`:

- `4:3`: upconverts to `1920x1080` with pillarbox so geometry stays correct
- `16:9`: upconverts PAL anamorphic widescreen to full-frame `1920x1080`

MP4 profiles are written as `1920x1080` at `25 fps`. Interlaced broadcast and ProRes profiles keep an HD interlaced recording pipeline when PAL is upconverted.

## Sony-Compatible MXF Notes

`XDCAM Sony Compatible` requires an FFmbc executable in the app folder. The app looks for:

```text
ffmbc.exe
ffmbc*.exe
```

For this profile, recordings are first written into:

```text
<RecordingDirectory>\<CameraOrStream>\.ffmbc-temp\<CameraOrStream>\<timestamp>
```

Completed clips are finalized with FFmbc in the background and moved into the recorder's dedicated subfolder. Do not delete the `.ffmbc-temp` folder while finalization is still running.

## Stream Recorder

The stream recorder panel accepts:

- Direct media URLs
- Local file paths
- YouTube page URLs
- Facebook / `fb.watch` page URLs

For YouTube and Facebook page URLs, copy `yt-dlp.exe` beside the app executable. The app uses it to resolve the actual media URL before starting FFmpeg.

Preview remains real-time for operator monitoring. Recording does not throttle input reads, so on-demand files and VOD URLs can be processed faster than real time while true live sources still record at live pace. When the source is finite, stream recording stops automatically at end of input.

Stream recordings are named like:

```text
Stream_ddMMyyyy_HHmmss.ext
```

## Clip Naming

DeckLink camera clips are written with camera-based timestamp names:

```text
CAM1_ddMMyyyy_HHmmss.ext
CAM2_ddMMyyyy_HHmmss.ext
CAM3_ddMMyyyy_HHmmss.ext
CAM4_ddMMyyyy_HHmmss.ext
```

The selected clip interval controls segment length. The interval can be set globally in the `COMMON` area or per camera/stream panel.

## Recording Folder

Default folder:

```text
C:\Users\<YourUser>\Videos\FFmpegRecorder
```

Use the `Recording Dir` field or `Browse...` button in the `COMMON` area to choose another folder. The setting is stored under the current Windows user profile and restored next time.

Each recorder writes into its own dedicated subfolder inside the selected root folder:

```text
<RecordingDirectory>\CAM1
<RecordingDirectory>\CAM2
<RecordingDirectory>\CAM3
<RecordingDirectory>\CAM4
<RecordingDirectory>\STREAM
```

## Requirements

- Windows
- x64 runtime/build target
- Blackmagic DeckLink hardware and Desktop Video drivers for camera capture
- DeckLink-enabled FFmpeg build
- `.NET 10` Windows Desktop runtime to run/build from source
- Optional: FFmbc for `XDCAM Sony Compatible`
- Optional: `yt-dlp.exe` for YouTube/Facebook stream-page recording

## Local Binary Requirements

Place these tools beside `FfmpegRecorder.exe`:

```text
ffmpeg.exe
ffplay.exe
ffprobe.exe
```

Optional tools:

```text
ffmbc.exe or ffmbc-*.exe
yt-dlp.exe
```

For a Debug build, the expected folder is:

```text
bin\Debug\net10.0-windows
```

This project intentionally uses local binaries from the app folder. It does not search `PATH`.

To verify that your FFmpeg build supports DeckLink:

```powershell
.\ffmpeg.exe -sources decklink
```

Generic Windows FFmpeg builds often do not include the `decklink` input.

## Build

```powershell
dotnet build FfmpegRecorder.vbproj
```

Default output:

```text
bin\Debug\net10.0-windows
```

The project keeps only a timestamped executable after each successful build, for example:

```text
FfmpegRecorder_20260428_165229.exe
```

## Run

Launch the timestamped executable:

```text
bin\Debug\net10.0-windows\FfmpegRecorder_yyyyMMdd_HHmmss.exe
```

Before recording, confirm the required FFmpeg tools are in the same folder as the executable.

## Basic Operation

1. Start the app.
2. Confirm each DeckLink source in the camera panels.
3. Choose a recording profile and clip interval in `COMMON`, or adjust individual panels.
4. Confirm the `Recording Dir`.
5. Choose the camera audio feed in `Listen Audio`, or set it to `Off`.
6. Use `Record All` or individual `Record` buttons.
7. Use `Stop All` or individual `Stop` buttons.
8. Use `Open Recordings` to open the output folder.

`Delete All` deletes recording files from all recorder subfolders under the selected recording directory only after all camera and stream recordings are stopped.

## Settings

Settings are stored under:

```text
C:\Users\<YourUser>\AppData\Roaming\FfmpegRecorder
```

Camera settings are stored per camera panel:

```text
settings-CAM1.txt
settings-CAM2.txt
settings-CAM3.txt
settings-CAM4.txt
```

The recording directory is stored in:

```text
recording-directory.txt
```

## Project Structure

| File | Purpose |
| --- | --- |
| `Program.vb` | Application startup |
| `RecorderHostForm.vb` | Main form, common controls, CPU display, shared recording folder |
| `RecorderHostForm.Designer.vb` | Main form designer layout |
| `Form1.vb` | DeckLink `RecorderControl` logic |
| `Form1.Designer.vb` | DeckLink recorder designer layout |
| `StreamRecorderControl.vb` | Stream, URL, and file recording panel |
| `RecorderOptions.vb` | FFmpeg argument generation for DeckLink recording and preview |
| `FfmpegProcessRunner.vb` | FFmpeg / FFplay process wrapper |
| `PreviewFrameReader.vb` | Pipe-based preview frame reader |
| `NetworkPreviewReader.vb` | TCP preview frame reader during recording |
| `RecordingDirectorySettings.vb` | Shared recording directory persistence |
| `FfmbcConversionQueue.vb` | Background FFmbc finalization queue |

## Repository

[https://github.com/vimlesh1975/FfmpegRecorder](https://github.com/vimlesh1975/FfmpegRecorder)
