<p align="center">
  <img src="assets/logo.png" alt="OpenM50 Webcam" width="600">
</p>

<p align="center">
  Use your <b>Canon EOS M50</b> as a webcam over a plain USB cable &ndash; in Zoom, Teams, Discord, OBS, your browser and every other app.<br>
  Free and open source. No EOS Webcam Utility (Pro) needed.
</p>

> **Not affiliated with, endorsed by or supported by Canon Inc.** "Canon" and "EOS" are trademarks of Canon Inc. and are used here only to describe compatibility. Use at your own risk.

## Features

- **Minimal app window** &ndash; live preview, camera status, and which apps are currently using the webcam or the OBS stream.
- **Real webcam device** &ndash; shows up as **"OpenM50 Webcam"** in every app (Windows 11), offered as 1920×1080, 1280×720, 1024×576 and 640×360 at 30 fps.
- **OBS-friendly stream** &ndash; low-latency MJPEG stream on `http://localhost:8090/` for OBS Browser/Media sources (also works on Windows 10).
- **No driver hacks** &ndash; talks to the camera through the stock Windows driver. No Zadig, no Canon SDK.
- **Clear status instead of a frozen picture** &ndash; if the camera is unplugged or switched off, the webcam image shows what's wrong ("No camera connected", …) and recovers automatically when the camera is back.
- **Graphical installer** with Start menu entry and clean uninstall.

## Honest limits

| | USB (this tool) | HDMI + capture card |
|---|---|---|
| Native resolution | 1024×576 (the camera's USB live view) | 1920×1080 |
| Frame rate | ~25 fps from the camera | up to 60 fps |
| Extra hardware | none | HDMI capture card |

**1080p60 over USB is not possible with the M50** &ndash; the camera simply does not send more over USB. Canon's own "Pro" utility also only upscales the same live view image. If you need true 1080p60, use the micro-HDMI output with a capture card (no software needed).

## Installation

1. Download **`OpenM50Webcam-Setup-x.y.z.exe`** from the [Releases](../../releases) page and run it.
   (Windows SmartScreen may warn because the installer is not code-signed: *More info → Run anyway*.)
2. Quit the **EOS Webcam Utility** if it is installed, and close EOS Utility / Windows Photos import.

Requirements: Windows 11 x64 for the webcam device; Windows 10 x64 works for the OBS stream only.

## Usage

1. Camera settings: switch **Wi-Fi/Bluetooth off** and set **Auto power off → Disable**.
   A dummy battery (DR-E12 + AC adapter) is strongly recommended for long sessions.
2. Connect the M50 via USB and switch it on.
3. Start **OpenM50 Webcam** from the Start menu. It finds the camera and starts live view automatically;
   the window shows a live preview and whether the camera, the webcam device and the OBS stream are in use (and by which app).
4. In your app, select the camera **"OpenM50 Webcam"**.
   Windows itself adds the suffix *"(Windows Virtual Camera)"* to every virtual camera's name; this cannot be changed by apps.
5. Click **Quit** when you're done &ndash; this also turns the camera's screen back on. The webcam device exists only while the app runs.

### OBS without the virtual camera

Lowest latency, no installation needed (also on Windows 10): **Sources → + → Browser**

- URL: `http://localhost:8090/`
- Width `1920`, height `1080`
- Uncheck "Shutdown source when not visible"

Alternatively **Media Source**, uncheck *Local File*, input `http://localhost:8090/stream.mjpg`.
Open `http://localhost:8090/?status` in a browser to see resolution and fps.

### Command-line options

Without options the app window opens. For automation and diagnostics:

```
--headless        run without window, log to the console (Ctrl+C to stop)
--port <n>        HTTP port (default 8090)
--fps <n>         limit frame rate (default: as fast as the camera delivers)
--keep-screen     keep the camera's own screen on while streaming
--lan             listen on all network interfaces (default: localhost only)
--no-vcam         do not create the "OpenM50 Webcam" virtual camera device
--device <text>   pick a device whose name/id contains <text>
--list            list all portable (WPD) devices and exit
--probe           grab frames for a few seconds, report fps, save probe.jpg
```

## Tips for a good image

- Use **movie mode** (or M mode) and set exposure, white balance and focus on the camera; the settings apply to the live view.
- Keep the shutter speed at **1/30 s or faster**, otherwise the live view frame rate drops in low light.
- Continuous AF (Movie Servo AF / Face+Tracking) works as usual.

## Troubleshooting

| Symptom | Fix |
|---|---|
| "No camera connected" | Check the USB cable, switch the camera on, Wi-Fi off. Run `OpenM50Webcam.exe --list` &ndash; the camera must appear as a portable device. |
| "Camera is not responding" | Another program (EOS Utility, EOS Webcam Utility, Lightroom tethering) may hold the camera. Close it, or switch the camera off and on. The app reconnects automatically. |
| Live view stops shortly after start | **Canon EOS Webcam Utility** grabs the camera when Windows re-scans cameras. The app recovers within a few seconds; uninstalling the EOS Webcam Utility avoids it completely (the app window shows a hint if it is installed). |
| "OpenM50 Webcam is not running" | Start OpenM50 Webcam from the Start menu. |
| Camera device not listed | Windows 11 is required; the device only exists while OpenM50 Webcam runs. |
| Camera switches off | Disable auto power off; use a dummy battery. |
| Something else | Run `OpenM50Webcam.exe --probe` and attach the output to a GitHub issue. |

## How it works

```
Canon EOS M50 --USB / PTP live view--> OpenM50Webcam.exe --MJPEG http://127.0.0.1:8090--> OBS (Browser/Media source)
                                                                                    \--> OpenM50VCam.dll (in Windows Frame Server)
                                                                                          --NV12--> "OpenM50 Webcam" in every app
```

- **OpenM50Webcam.exe** (C#/.NET 8, WPF window) reads the live view with Canon's PTP vendor commands (as documented by [libgphoto2](https://github.com/gphoto/libgphoto2)) through the Windows Portable Devices API, serves it as MJPEG and registers the virtual camera via `MFCreateVirtualCamera`.
- **OpenM50VCam.dll** (C++) is a Media Foundation media source loaded by the Windows Frame Server. It decodes the MJPEG stream with WIC, scales it and delivers NV12 frames &ndash; or a status screen when no image is available.

## Building from source

Requirements (Windows 10/11 x64):
- [.NET 8 SDK](https://dotnet.microsoft.com/download)
- Visual Studio 2022 or Build Tools with the **Desktop development with C++** workload and a **Windows 11 SDK**
- [Inno Setup 6](https://jrsoftware.org/isinfo.php) (optional, for the installer)

```powershell
.\build.ps1
```

Output in `dist\`: `OpenM50Webcam.exe`, `OpenM50VCam.dll` and `OpenM50Webcam-Setup-<version>.exe`.

Other files: `assets\make-icon.ps1` regenerates the icon and logo; `src\OpenM50VCam\vcamtest.cpp` is a diagnostic tool that opens the virtual camera like a normal app would.

## Other cameras

Other Canon EOS / EOS M bodies with PTP live view (e.g. many EOS DSLRs, M6, M200) will likely work as well, but only the M50 has been tested. Reports are welcome.

## License

[MIT](LICENSE)
