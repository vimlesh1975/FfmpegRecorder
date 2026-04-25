# FfmpegRecorder

Windows desktop multi-camera recorder for Blackmagic DeckLink inputs, built with VB.NET WinForms and local FFmpeg binaries.

This app is designed for simple operator use: live preview, per-camera or global record control, audio listen selection, and timestamped clip recording for up to 4 camera panels.

## What It Does

- Records DeckLink sources to timestamped clips such as `CAM1_25042026_160045.mxf`
- Supports 4 camera panels: `CAM1`, `CAM2`, `CAM3`, `CAM4`
- Shows live preview with left/right vertical audio level bars
- Lets you record one camera or all cameras together
- Lets you listen to one selected camera audio feed at a time
- Shows per-camera CPU usage plus total PC CPU
- Saves each camera's DeckLink, profile, and interval settings
- Uses only local `ffmpeg.exe`, `ffplay.exe`, and `ffprobe.exe` from the app folder

## Main Features

- Shared `COMMON` control area
  - Profile selection
  - Clip interval selection
  - `Record All`
  - `Stop All`
  - `Open Recordings`
  - `Delete All`
  - `Listen Audio`
  - CPU summary for all cameras and the PC
- Individual camera controls
  - DeckLink selector
  - Status
  - Profile
  - Interval
  - `Record`
  - `Stop`
  - Live FFmpeg log output
- Visual operator layout
  - Each camera panel has its own color theme
  - Shared `COMMON` area has a distinct theme

## Default Camera Mapping

The app prefers these inputs by default:

| Camera | Preferred DeckLink input |
| --- | --- |
| `CAM1` | `DeckLink SDI 4K` |
| `CAM2` | `DeckLink Duo (1)` |
| `CAM3` | `DeckLink Duo (2)` |
| `CAM4` | `DeckLink Duo (3)` |

The app also prevents two panels from silently using the same DeckLink input at the same time.

## Recording Profiles

All current profiles record at `1920x1080`.

| Profile | Container | Notes |
| --- | --- | --- |
| `XDCAM HD422` | `MXF` | Broadcast-style MPEG-2 4:2:2 edit format |
| `MP4 High Quality` | `MP4` | `1080p25`, higher quality, larger files |
| `MP4 Low Bitrate` | `MP4` | `1080p25`, lighter files, lower bitrate |
| `ProRes Proxy (Small)` | `MOV` | Smallest ProRes option |
| `ProRes LT (Light)` | `MOV` | Lighter edit-friendly ProRes |
| `ProRes 422 (Medium)` | `MOV` | Balanced ProRes profile |
| `ProRes 422 HQ (High)` | `MOV` | Highest quality ProRes profile in this app |

### MP4 Note

Both MP4 profiles are `1920x1080` at `25 fps`.

The difference is compression:

- `MP4 High Quality` uses better quality settings and more CPU
- `MP4 Low Bitrate` uses lighter settings and smaller files

## Clip Naming And Storage

Clips are written with camera-based timestamp names:

```text
CAM1_ddMMyyyy_HHmmss.ext
CAM2_ddMMyyyy_HHmmss.ext
CAM3_ddMMyyyy_HHmmss.ext
CAM4_ddMMyyyy_HHmmss.ext
```

Default recordings folder:

```text
C:\Users\<YourUser>\Videos\FFmpegRecorder
```

## Requirements

- Windows
- Blackmagic DeckLink hardware and Desktop Video drivers
- A DeckLink-enabled FFmpeg build
- `.NET 10` Windows Desktop runtime if you build/run from source

## Important FFmpeg Requirement

This project depends on FFmpeg builds that include DeckLink support.

Generic Windows FFmpeg builds often do **not** include the `decklink` input. If FFmpeg cannot open DeckLink devices, verify your local binary with:

```powershell
ffmpeg -sources decklink
```

The app is intentionally configured to use only the local binaries placed beside the executable:

```text
bin\Debug\net10.0-windows\ffmpeg.exe
bin\Debug\net10.0-windows\ffplay.exe
bin\Debug\net10.0-windows\ffprobe.exe
```

It does not fall back to `PATH`.

## Build From Source

```powershell
dotnet build FfmpegRecorder.vbproj
```

Default build output:

```text
bin\Debug\net10.0-windows
```

## Run

After build, launch:

```text
bin\Debug\net10.0-windows\FfmpegRecorder.exe
```

Make sure these files are present in the same folder:

- `ffmpeg.exe`
- `ffplay.exe`
- `ffprobe.exe`

## How To Use

1. Start the app.
2. Confirm the DeckLink source for each camera panel.
3. Pick a shared profile and interval in the `COMMON` area, or change them per camera.
4. Choose which camera audio you want to listen to in `Listen Audio`.
5. Use `Record All` or the individual `Record` buttons.
6. Use `Open Recordings` to open the output folder.

## Settings Persistence

Each camera stores its own settings under the current Windows user profile, including:

- selected DeckLink input
- selected profile
- selected interval

These settings are restored the next time the app opens.

## Notes

- Only one camera audio source is monitored at a time.
- A single DeckLink input should not be opened by multiple recorder panels at once.
- `Delete All` is blocked while any recording is active.
- The app records segmented clips based on the selected interval.
- MP4 segment timing is aligned using forced keyframes so clip lengths follow the requested interval more closely.

## Project Structure

| File | Purpose |
| --- | --- |
| `Form1.vb` | `RecorderControl` logic |
| `Form1.Designer.vb` | `RecorderControl` UI |
| `RecorderHostForm.vb` | Main host form logic |
| `RecorderHostForm.Designer.vb` | Main host form UI |
| `RecorderOptions.vb` | FFmpeg argument generation |
| `FfmpegProcessRunner.vb` | FFmpeg / ffplay process management |
| `PreviewFrameReader.vb` | Idle preview reader |
| `NetworkPreviewReader.vb` | Recording preview stream reader |
| `Program.vb` | Application startup |

## Included Binaries

This repository currently tracks the packaged app output and FFmpeg tools in the default build folder for convenience.

Bundled FFmpeg license file:

```text
bin\Debug\net10.0-windows\LICENSE
```

## GitHub

Repository:

[https://github.com/vimlesh1975/FfmpegRecorder](https://github.com/vimlesh1975/FfmpegRecorder)
