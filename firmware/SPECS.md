# Specification audit: original HE SIM PEDALS and OpenPedalControl

This applies to the photographed **EFM8UB10F16G-QFN28 pedal board**, not the PIC-based
BU0836-LC handbrake. The comparison combines the manufacturer's MCU documentation,
the owner's verified original backup and USB captures, and our source and bench measurements.
It does not establish complete functional or latency parity with the original controller.

## Resolution, rate and features

| Item | Original board/application | OpenPedalControl firmware 1.13.0 |
| --- | --- | --- |
| MCU | EFM8UB10F16G-QFN28, 16 KiB flash | Same physical MCU |
| ADC resolution | Nominal 12 bits | Nominal 12 bits; no claim of extra effective precision |
| USB logical axis range | 0–4095 | 0–4095 |
| Axis storage in the HID report | 16-bit fields | 16-bit fields; these do not make the ADC 16-bit |
| Load-cell ADC channels | LC1/2/3: mux 14/13/12 | Same, confirmed by continuity and independent raw movement |
| ADC reference and acquisition | External P0.0/VREF; gain 1; ADC0CF 0x13, ADC0AC 0x82, ADC0PWR 0x4F, ADC0TK 0xBF | Same settings; two accumulated 12-bit results are averaged, native range 0–4092 |
| USB speed | Full-speed USB | Full-speed USB |
| Interrupt endpoint interval | 4 ms: nominal maximum polling rate 250 Hz | Same 4 ms; firmware schedules at most once per four USB frames |
| Input payload | 16 bytes: seven axes and twelve buttons | 8 bytes: three axes and twelve reserved, inactive button bits |
| Working inputs | Three load cells, four extra analog axes, twelve buttons | Three load cells only; extra axes and buttons are not implemented |
| Curves | Original calibration facilities | Six output points per pedal, deadzones, brake max force and source mapping stored in flash |
| Software configuration | Original Windows tool/calibration | Saved profile and raw samples accessible through USB HID Feature |
| Firmware update path | Preserved AN945-compatible USB bootloader | Same bootloader, plus software entry from our application |

The Silicon Labs data sheet specifies a **12-bit ADC**. A 21-bit range would mean over two million
codes; it is not provided by this ADC or by our firmware. Nominal resolution also differs from usable
precision in the presence of sensor and analog noise. [EFM8UB1 data sheet, analog specifications](https://www.silabs.com/documents/public/data-sheets/efm8ub1-datasheet.pdf)

The original application USB configuration descriptor at backup offset `0x203B` declares endpoint
`0x81`, 64-byte maximum packet size and `bInterval=4`. The separate bootloader descriptor at
`0x3A20` uses 1 ms; this is **not** the original pedal application's report interval. Our descriptor
keeps the application's 64-byte endpoint and 4 ms interval.

## CPU clock and timing limits

Our tested application selects **HFOSC0 at 24.5 MHz**. Static inspection of the original application
finds a clock-selection routine at `0x19BA`: initialization calls it with a value selecting HFOSC0/2
(12.25 MHz), and its acquisition path can select HFOSC1 at 48 MHz with the corresponding prefetch
configuration. This is evidence of different clock settings, not a measurement of its runtime duty cycle.

Thus our CPU-clock configuration is **not identical** to the original. Matching the 4 ms USB interval
does not prove matching acquisition latency, jitter, filtering or suspend behavior. Raising SYSCLK
would require the documented clock-switch sequence and flash prefetch settings, followed by hardware
tests. It is not enabled merely to advertise a larger MHz figure. The burst ADC uses its own HFOSC0
clock settings. [EFM8UB1 reference manual, clocking, prefetch and ADC sections](https://www.silabs.com/documents/public/reference-manuals/efm8ub1-rm.pdf)

An eight-second **idle/noise** HID capture on 2026-11-16 received 1,474 reports, about 184/s.
The median interval was 4.007 ms, the 95th percentile 8.014 ms, and the maximum 12.032 ms.
Our firmware suppresses unchanged reports, so this does not measure maximum throughput or
end-to-end pedal latency. Controlled motion, acquisition-to-host latency and jitter under configuration
traffic remain to be measured. The desktop raw-snapshot view polls at about 50 Hz; it does not set the
separate game interrupt endpoint's nominal 250 Hz polling interval.

## What has been checked

- The current image rebuilds to the byte-identical HEX SHA-256
  `ed64c2201542dcb76b32ef1c8ffcacd7fba81ffdd41994162e6b268f43a2c226`.
- Three raw sensor inputs move independently. Saved nonlinear curves were compared with 60 real
  raw/cooked snapshots; those samples matched the desktop calculation.
- Desktop versus firmware math also passed 344,064 host comparisons, with at most one output count
  of rounding difference out of 4095.
- Profile saving and persistence across a software reset were checked. The owner's subsequent guided
  calibration records all three sensor directions as decreasing with pressure. The profile remained
  byte-identical after the owner physically disconnected and reconnected USB. The outputs returned
  near zero at rest; the brake showed around 1% residual with zero deadzone, which calls for a small
  bottom deadzone rather than increasing ADC bit depth.
- The native desktop assistant entered the original bootloader, verified the full current application and
  protected-region CRCs, and restarted into the pedal application. Its saved calibration was byte-identical
  before and after this read-only test. No erase or program command was sent during that check.
- Original Python application programming was exercised on the board. The new native write engine
  is tested against a separate flash model, including loss of connection before and after each command.
  A real write through the new desktop assistant and writing the original backup back to the board
  have not yet been tested.

Still pending: suspend current and resume tests, controlled-motion latency measurements, and
auxiliary-input/button support.

## Future updates

`version.h` supplies the version for the USB device descriptor and the build manifest. The manifest
binds an image checksum to the board target, profile version, ADC mapping, resolution and declared
report interval. These declarations are build metadata, not a signed package or an independent
measurement of an arbitrary supplied image.

The desktop assistant rejects incompatible targets or profile layouts before USB access. It checks
the chip and preserved-region CRCs again immediately before writing. It preserves the original
bootloader at `0x3A00–0x3FFF` and both security pages at `0xFB80–0xFBFF`; a custom application
update also preserves user data. Address zero is activated only after the rest of the image passes
verification. No Lock command is implemented. This leaves the existing USB update route available
for later revisions. A damaged bootloader would still need C2 recovery.

Future changes should retain profile version 1 when compatible; a changed layout needs an explicit
conversion and corresponding desktop update. More features, higher CPU clocks or a faster report
interval require new measurements and regression checks rather than a version-number change alone.
