# Onboard pedal firmware

This prototype targets the Silicon Labs EFM8UB10F16G-QFN28 on the blue `HE SIM PEDALS` board.
It samples three load-cell inputs, applies the saved profile, and sends a USB HID joystick report
with throttle on X, brake on Y and clutch on Z. The desktop app is needed to configure the board.
Once saved, the profile is applied by the controller at power-up.

The EFM8 application builds with SDCC. On one board, USB programming, DirectInput enumeration,
three independent ADC inputs, profile readback and persistence across a software reset have been
tested. Sixty raw/cooked sample comparisons with a saved nonlinear curve matched the desktop math.
Guided user calibration and persistence after a physical USB power cycle were subsequently verified.
Suspend current and controlled-motion timing measurements remain pending. This version implements LC1–LC3. The four expansion axes, twelve switch
inputs and the separate PIC-based BU0836-LC handbrake are outside this target.

This is experimental bench firmware. The desktop release only updates the Windows app; it does not
flash a device. The USB programming tools are pinned to the verified original backup of the bench
unit and are not a generic installer for other controllers. Original firmware and backups are not included.

The [specification audit](SPECS.md) distinguishes 12-bit ADC resolution from 16-bit HID fields,
the nominal 4 ms USB report interval and CPU clocks. It records measured behavior and remaining
differences, including the four auxiliary axes and twelve button inputs that are not implemented.

## Desktop USB firmware assistant

Available through **Settings → Pedal firmware…**. Since 0.4.2 the firmware is included in the app;
there is no separate download or file selection in the normal workflow. The assistant uses
Windows HID directly; Python and a C2 adapter are not needed for this USB workflow. It currently
requires the verified bench-board backup. Other boards and the PIC handbrake are rejected.

1. Open the assistant. It shows the **installed** and **included** firmware versions. If it says
   **Already installed**, you can close it: the firmware is already on your pedals.
2. The original backup is found and hash-verified automatically under
   `Documents/OpenPedalControl-backups/pedalier/<unit>/<date>`, or in a previously chosen folder.
   If needed, click **Locate backup…** once. Original firmware is never included in the app or uploaded.
3. When an installation is needed, click **Prepare installation**. Our firmware enters update mode
   automatically, and the assistant checks the chip and preserved-region CRCs. With original firmware,
   follow the request to hold FW/reset while reconnecting USB, then click **Continue**.
4. Click **Install firmware…**, review the version and confirm. Keep USB connected until completion.
   Custom updates preserve the saved calibration and curves. Closing before any write returns a
   controller that the assistant put into update mode to its existing application.
5. Close the assistant after completion and check the pedals. Calibrate/save if the wiring or sensors changed.

**Advanced options** holds external firmware files, explicit reinstallation, original restoration,
technical details and manual controller checks. Developers can select `pedals.firmware.json` next to
`pedals.ihx` there; ordinary users use the included firmware. The manifest is build metadata, not a
digital signature or proof that an arbitrary external image is trustworthy.

All 29 application pages are replaced during installation; the reset-vector first byte is activated last.
Image and protected-region CRCs must pass before restart. Chip ID `0x3241`, bootloader version
`0x90`, image integrity, address limits and profile compatibility checks remain required.

**Restore original:** expand **Advanced options** and select the restoration checkbox. The app builds the
restoration plan from the verified backup in memory, replacing the original application and all fourteen
user-data pages at `0xF800–0xFB7F`. It preserves the bootloader and protection pages. Actual restoration
writing remains untested on hardware; it is covered by the simulated flash tests.

The session log is in `%LOCALAPPDATA%\OpenPedalControl\firmware-sessions`. There are no automatic
retries or restart attempts after a failed write. **Restart controller** requires a read-only CRC match
against the selected complete image. Reconnect holding FW/reset for a new recovery attempt. The
assistant cannot repair an erased/damaged bootloader; that still needs a C2 programmer.

The native assistant's software boot entry, read-only full-image/protected-region verification, restart and
unchanged saved calibration were checked on the real board. Its write engine has passed simulation,
including disconnects before and after every command. A real write through this new UI is still pending.
Automatic preparation and cancellation back to normal mode were also checked on the board with zero
erase/program commands and unchanged calibration.

## Building for a board

Use Python 3 and SDCC with the MCS-51 backend. From the repository root, run
`python firmware/efm8/build.py --vid VID --pid PID` with the USB identifiers for the prototype.
The photographed HE SIM PEDALS board defaults to ADC0MX **14, 13, 12** in **LC1, LC2, LC3 order**.
For another wiring layout, pass `--mux` with its three channel numbers. The script produces an Intel HEX image and link map
in `firmware/efm8/bin/`. It only builds the image.

The bench uses private test identifiers `1209:0002` and serial `OPC-TEST0001`. These are not a unique
USB identity assigned to this project for redistribution. Select appropriate USB identifiers and a unique
serial before deploying to other units; the build requires explicit VID/PID arguments.

The [board measurements](HARDWARE.md) document the three confirmed amplifier connections. Disassembly of the recovered
original confirms the external P0.0/VREF reference and a gain of one for the load-cell inputs.
The target now uses these settings. The physical reference voltage has not been measured.
The same notes cover the manufacturer's documented backup options for both controllers. The
[EFM8 build and bench notes in French](efm8/README.md) describe the target settings in more detail.

## Using the profile

1. On a bench unit running this firmware, open **OpenPedalControl Pedals** in the desktop app.
   The editing screen reads the profile currently on the controller.
2. Use **Calibrate…** or assign physical inputs to throttle, brake and clutch. Set each input's
   released and pressed values, deadzones, brake max force, and six curve points. Inverted sensors
   are accepted. Choosing **None** silences that game axis.
3. **Save to controller** writes the entire profile and verifies the readback. Close the app and
   bind the physical joystick's axes in the simulator.

For the first bench test, verify enumeration and movement with one known input at a time. Save a
recognizable curve, disconnect power, then reconnect with the app closed and check the cooked
axis in a joystick viewer. Reopen the app and compare the profile with the saved settings.

## HID reports (version 1)

The current USB transport is unnumbered. Lengths below include the leading zero report-ID byte
required by Windows HID APIs. Multi-byte numbers use little-endian order.

| Report | Bytes | Contents |
| --- | --- | --- |
| Input 0 | 9 | Zero, two reserved button bytes, then three cooked 16-bit joystick axes (0–4095), X/Y/Z. |
| Feature 0 | 57 | Zero, ASCII `OPC`, version 1, output count 3, three 13-byte configurations ordered X/Y/Z, then three cooked and three raw 16-bit samples. GET returns one coherent snapshot. SET validates and saves the profile; the final twelve bytes are ignored. |

The input descriptor declares X/Y/Z usages individually inside its physical collection. This shape
enumerated successfully in DirectInput on the bench. The app normalizes the first 45 feature bytes to
an internal profile with ID 2. Raw readings occupy Windows feature offsets 51–56, in LC1/LC2/LC3
connector order regardless of output mapping. The desktop codec also accepts early numbered
13/45-byte prototypes; current firmware builds use the 9/57-byte transport above.

Each axis record contains a one-byte source (0–2 or 255 for off), two 16-bit sensor endpoints
(rest then fully pressed), one byte each for bottom and top deadzone percentages, and six
output percentages at 0, 20, 40, 60, 80 and 100 percent travel. Brake max force uses the same
upper endpoint adjustment as the desktop app. A brake max force of 70 percent encodes a top
deadzone of 30 percent. Validation rejects conflicting inputs, an enabled axis with equal endpoints,
and deadzones that eliminate the active range. The neutral profile maps LC1–LC3 to X–Z with a
linear curve and sensor endpoints 0 and 4092 (the EFM8 ADC's 12-bit burst-mode range).

The firmware alternates between two nonvolatile slots, each with a generation number, CRC and commit
marker. A save leaves the previous slot intact until the new one has been written and checked.

## Testing

`dotnet test -c Release` exercises the Windows app, including the profile codec and onboard curve
preview. With .NET 10 and a host C compiler, `python firmware/tests/run.py` also compiles the
production C code and compares it against the desktop calibration math across all 4,096 input values.

`python -m unittest discover -s firmware/tests -p "test_*.py"` checks Intel HEX integrity,
protected address ranges and the application-only update engine. A simulated flash target exercises
successful programming, restoration, bad CRCs, unexpected responses and a disconnect at every transfer.
The Windows [USB updater](efm8/README.md#outil-usb-de-transfert-et-de-restauration) has separate
read-only inspection/verification and explicit programming commands. Its read-only path has been
checked against the original controller. Application programming and protected-region CRC checks
passed on the bench. Writing the original backup back to the board has not been tested. The original
bootloader and security pages are excluded from USB writes; damage would require C2 recovery.

`python firmware/efm8/onboard_check.py` reads the profile and live snapshot without saving anything.
`enter_bootloader.py` requests a software return to the preserved bootloader for later bench updates.
`build.py --diagnostic-no-feature` builds a separate bench diagnostic that returns to the bootloader
after ten seconds; it is not the normal pedal application.
