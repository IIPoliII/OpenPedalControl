# OpenPedalControl

[![Build](https://github.com/IIPoliII/OpenPedalControl/actions/workflows/build.yml/badge.svg)](https://github.com/IIPoliII/OpenPedalControl/actions/workflows/build.yml)

A small Windows tool to calibrate and tune first-generation sim racing pedals and handbrakes,
including the original Heusinkveld Sim Pedals (`HE SIM PEDALS`) and Heusinkveld handbrake
(Leo Bodnar BU0836-LC interface).

## Features

- Guided calibration detects which input moved and remembers the furthest press.
- Released and pressed endpoints, bottom and top deadzones, and brake max force.
- Windows calibration with a one-bend curve for original controller firmware.
- Six-point curves and presets stored on controllers running the experimental OpenPedalControl pedal firmware.
- Live raw sensor readings and a preview of the settings being edited.
- GitHub update checks, verified downloads and an embedded changelog.

Saved settings work with the app closed. No virtual controller or extra driver is required.

## Download

From the [latest release](https://github.com/IIPoliII/OpenPedalControl/releases/latest):

- **`OpenPedalControl-Setup-x.y.z.exe`** installs the app and shortcuts.
- **`OpenPedalControl.exe`** is the portable app. The portable ZIP also includes this README and the license.

Windows 10 or 11 (64-bit), with the Windows .NET Framework 4.8. Versions 0.2.0 and later can
update through **Settings → Check now**. From 0.1.0, download the new release manually.

## Where settings are saved

The app detects the controller firmware and shows its available editor automatically.

| | Original firmware | OpenPedalControl pedal firmware |
| --- | --- | --- |
| Save target | Windows | Controller flash |
| Curve | One bend, adjustable midpoint | Six points and presets |
| Works with the app closed | Yes | Yes |
| Device to bind in your sim | Physical controller | OpenPedalControl Pedals: X throttle, Y brake, Z clutch |

**Original firmware**, including the BU0836-LC handbrake, uses DirectInput calibration, as DIView does.
Games that bypass Windows calibration and read raw input ignore these settings. On another PC,
calibrate again. Profiles are kept in `%APPDATA%\OpenPedalControl\profiles`.

**Controller firmware mode** reads its profile and raw LC1–LC3 measurements over USB. Choose a sensor
for each pedal, including inverted sensors, then use **Save to controller**. The game receives the saved
curve directly from the physical controller. Unsaved edits are shown as a preview.

The app update does **not** flash a controller. The [experimental firmware and hardware notes](firmware/README.md)
are for the EFM8 pedal board only. The PIC handbrake stays on its original firmware.

Version 0.3.0 removes virtual output and its bundled driver. Older virtual profiles open in Windows mode;
the six-point curve cannot be applied by Windows, which uses the stored midpoint instead. Save to Windows
and bind the physical controller in your sim.

## Using it

1. Connect the pedals or handbrake and select the device.
2. Click **Calibrate…** and follow the steps. Press only the requested pedal or lever.
3. Adjust deadzones, max force and the curve. Watch the raw readings and preview.
4. Click **Save to Windows** or **Save to controller**.
5. Bind the physical device in your sim. Restart the sim after changing its Windows calibration.

Some sims also have their own calibration; run it again after changing these settings.
The legacy sensors do not report kilograms, so force is shown as raw values and percentages.
This app is intended for pedals and levers, rather than self-centering steering axes.

**Settings** contains optional startup in the notification area, update checks and **What's new**.
Neither output mode needs the app to start with Windows or run while driving.

## Supported devices

| Device | Status |
| --- | --- |
| First-generation Heusinkveld Sim Pedals (`HE SIM PEDALS`, USB `10C4:8B02`) | Windows calibration tested |
| First-generation Heusinkveld handbrake (BU0836-LC, USB `16C0:05B7`) | Windows calibration tested; original firmware retained |
| EFM8 pedal board with OpenPedalControl firmware | Experimental; USB, three sensors and saved curve output tested on one board |
| Other HID game controllers with analog axes | Shown with one entry per axis |

## Building

Requires the [.NET SDK 10](https://dotnet.microsoft.com/download) on Windows.

```text
dotnet build -c Release
dotnet test -c Release
```

The app is built in `src/OpenPedalControl/bin/Release/net48/`. The installer additionally needs
[Inno Setup 6](https://jrsoftware.org/isinfo.php):

```powershell
./installer/build-installer.ps1 -Version 0.3.0
```

Pushing a version tag makes GitHub Actions build, test and publish the app. Add the release to
[CHANGELOG.md](CHANGELOG.md) first; it is embedded as **What's new**. Firmware compilation and bench
testing are documented separately in [firmware/README.md](firmware/README.md).

## Disclaimer and license

OpenPedalControl is an independent open source project, unaffiliated with Heusinkveld Engineering
or Leo Bodnar. Manufacturer firmware images and personal device backups are not distributed.
Product names identify compatible hardware and belong to their respective owners.

[MIT](LICENSE)
