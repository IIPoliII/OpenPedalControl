# Changelog

## 0.5.0 - 2026-11-16

- The installer is now the only download; the portable exe is discontinued. A portable copy updating from inside the app opens the setup wizard instead.
- App updates are signed with the OpenPedalControl release key and are only run when the signature verifies, in addition to GitHub's checksum. The installer is started without going through the command prompt, so unusual folder names no longer break updates.
- Windows calibration: bottom and top deadzones (or brake max force) can no longer add up to 100 % or more, which used to swap the active range and invert the curve.
- Uninstalling removes only the app's own files, never other files in the chosen folder, and removes "Start with Windows" for the user who enabled it even when an administrator account confirms the uninstall.
- Unsaved edits are no longer lost silently: switching device or pressing Refresh asks first, and edits on a controller-firmware device survive a USB reconnection. Profile and settings files are replaced in one step, so a crash while saving cannot erase them.
- Two identical controllers without a serial number keep separate Windows profiles while both are connected.
- Guided calibration: an input that did not report during the release step no longer records a false rest position.
- Firmware assistant: a failure before anything is written still returns the pedals to normal mode when the window is closed, and keeps Restart available. A device disappearing during the switch to update mode no longer aborts detection.

## 0.4.2 - 2026-11-16

- Firmware is now included in the app: no separate download, ZIP extraction or manifest selection is needed. The portable exe contains the same tested pedal firmware 1.13.0.
- The firmware screen shows installed and included versions and avoids offering an unnecessary reinstall. Original backups are discovered and verified automatically in the existing backup folder, or can be located once and remembered.
- One Prepare installation button handles update mode and controller checks before final confirmation. Manual files, restoration and reinstall options are under Advanced options.
- Closing before any flash write returns a controller that this assistant put in update mode to its existing application. Failed write sessions still never restart automatically.

## 0.4.1 - 2026-11-16

- Block app exit and app update installation while the firmware assistant is open. Firmware maintenance cannot start during an app update, and background device refresh stays paused until it ends.
- This stable USB-assistant release includes the 0.4.0 changes below. The preliminary 0.4.0 build is superseded by this release with additional interruption guards.

## 0.4.0 - 2026-11-16

- Settings now includes a USB pedal firmware assistant: select a versioned firmware bundle and the verified original backup, check the controller, then review and explicitly confirm installation. No Python or external programmer is needed.
- Custom updates preserve profile data, the original bootloader and protection pages. The assistant can also restore the original application and user settings from the local backup. It currently supports the verified EFM8 bench board only; the PIC handbrake is excluded.
- Image integrity, target, profile format, ADC mapping and declared specifications are checked before USB operations. Chip identity and protected-region CRCs are checked before programming and again afterward. A failed session never retries or restarts automatically.
- Published a specification audit distinguishing 12-bit sensor resolution, 16-bit USB fields, the 4 ms report interval and CPU clocks. Guided calibration already handles inverted sensors independently; a regression test now also covers mixed directions and swapped sensor wiring.
- Firmware 1.13.0 remains the tested bench application. The assistant changes the update workflow; it does not silently install a new device firmware or increase its resolution or report rate.

## 0.3.0 - 2026-11-16

- New controller firmware mode: read and save calibration, sensor assignments, deadzones, brake max force and six-point curves directly on supported OpenPedalControl pedal firmware. Saved settings work with the app closed.
- Live raw sensor readings and guided calibration support the three load-cell inputs, including sensors whose values decrease when pressed. Settings are verified after saving.
- Removed vJoy output, device settings, driver downloads and bundled driver. Original pedal and handbrake firmware uses Windows calibration with a one-bend curve; former virtual profiles now open in this mode.
- The app update does not install or replace any device firmware. The experimental EFM8 firmware sources, hardware notes and application-only USB tools are published separately in the repository. No PIC handbrake firmware is included.

Shown inside the app ("What's new"). Each release is a `## version - date` heading followed by `- ` lines.

## 0.2.1 - 2026-11-12

- Installer: when vJoy is already installed, a note now says so instead of the vJoy option silently missing. Your existing vJoy setup is left as it is.
- Updates are checked against the SHA-256 checksum GitHub publishes for each release before they are run.
- The first-generation Heusinkveld handbrake is now tested.

## 0.2.0 - 2026-11-12

- New output mode: virtual controller (vJoy) with a fully custom 6-point curve and ready-made presets (Progressive, Degressive, S-curve...).
- The Windows calibration mode stays available: it works with the app closed, with a one-bend curve.
- Choose the mode per device. Pedals sent to vJoy keep working while the app sits in the notification area.
- Pick which vJoy device to use. "Automatic" takes the first free one, so a device used by another program is left alone.
- Settings window: start with Windows, vJoy device, "Configure vJoy", updates and this changelog.
- Updates: check at startup (can be turned off), check now, and update in one click. After an update the app restarts in the tray and tells you with a notification.
- Installer with an optional vJoy driver, Start menu and desktop shortcuts, and start with Windows.
- The first Heusinkveld handbrake (Leo Bodnar BU0836-LC) is recognised as a handbrake.

## 0.1.0 - 2026-11-12

- First release: guided calibration that finds which input each pedal uses.
- Bottom and top deadzones, brake max force and a one-bend output curve.
- Live raw and output bars, and a curve graph with the current position.
- Saved as the standard Windows calibration, like DIView. Works with the app closed.
