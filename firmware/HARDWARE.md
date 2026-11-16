# Legacy pedal and handbrake controllers

## Heusinkveld pedal controller

The blue board marked `HE SIM PEDALS` has three load-cell sockets (`LC 1` through `LC 3`), four additional analogue inputs, twelve button inputs and a USB-B connector. U4 reads `EFM8 / UB10F16G`. Its seven contacts per edge identify the QFN28 package. Silicon Labs specifies 16 KiB flash and a 12-bit ADC for this part. [EFM8UB1 data sheet, sections 2 and 6.1](https://www.silabs.com/documents/public/data-sheets/efm8ub1-datasheet.pdf)

The three eight-lead circuits U1–U3 sit between the load-cell sockets and U4. Their `Y11` marking, package and surrounding filter components match the Analog Devices AD8293G160ARJZ, a fixed-gain (160 V/V) instrumentation amplifier. Pin 1 is −IN, pin 8 is +IN, pin 4 is ADC OUT, and pin 6 is the output before the internal 5 kΩ series resistor. The PCB tracks and continuity measurements determine which output feeds the MCU on each channel. [AD8293G160 data sheet, figure 3, table 6 and ordering guide](https://www.analog.com/media/en/technical-documentation/data-sheets/AD8293G80_160.pdf)

### Load-cell channels on the photographed board

With USB-B at the bottom, viewed from the component side, the dot on U4 is in the bottom-left corner.
Pin 1 is on the bottom edge. Numbering runs counterclockwise around the package. The seven contacts
on the **top** edge are **21, 20, 19, 18, 17, 16, 15** from left to right.
The owner's unpowered continuity measurement places U3.4, U2.4 and U1.4 on consecutive contacts.
The owner confirmed that U1 pin 4 reaches the second contact from the right, **U4 pin 16**.
The track from C7 reaches this contact in the unmarked photo. The U1 arrow in the annotated
photo is one contact to its right. [QFN28 pin numbers and analog functions, figure 6.1 and table 6.1](https://www.silabs.com/documents/public/data-sheets/efm8ub1-datasheet.pdf)

| Physical input | Amplifier output | U4 contact (top edge, from left) | U4 GPIO | ADC0MX |
| --- | --- | --- | --- | --- |
| LC1 (J1) | U1 pin 4 | 6th, pin 16 | P1.6 | 14 (0x0E) |
| LC2 (J2) | U2 pin 4 | 5th, pin 17 | P1.5 | 13 (0x0D) |
| LC3 (J3) | U3 pin 4 | 4th, pin 18 | P1.4 | 12 (0x0C) |

The firmware uses this order by default. The first energized bench check is one moving raw LC reading per
physical sensor, followed by assigning each connector to a pedal in the calibration wizard.

### Additional board measurements

Record the PCB revision and mark the pin-1 corners on a component-side photo. Disconnect USB and sensor cables before measuring resistance. Measure the resistance of the shorted meter probes first so a direct copper connection is distinguishable from a path through a component.

1. For each LC socket, trace its two signal contacts to the amplifier input pins. Where a resistor intervenes, identify its two ends and record its value. Number the socket contacts on the photo so the mating view can be reconstructed.
2. At that amplifier, start at pin 4. Trace via an accessible pad to the U4 contact, recording the resistance and any intervening part. If the direct path starts at pin 6 instead, record that pin. Use U4's **QFN28** pin table to translate the measured pin into its port name and ADC0 channel. [EFM8UB1 data sheet, table 6.1](https://www.silabs.com/documents/public/data-sheets/efm8ub1-datasheet.pdf)
3. Reconnect the original controller and move one pedal at a time. Record the physical pedal, changing USB axis, raw rest and loaded values, and direction. The plugged-in cable determines the pedal role. Record analogue supply and reference evidence separately before selecting an ADC reference for replacement firmware.

### Preserving the installed firmware

The four underside pads carry the labels TP1–TP4. Their signals still need a continuity measurement. U4's documented reference pins are 3 (GND), 6 (VDD), 9 (RST/C2CK) and 10 (P3.0/C2D). Identify the actual board connections and target supply before attaching a C2 adapter. The `FW/reset` legend alone does not identify a particular bootloader. [EFM8UB1 data sheet, table 6.1](https://www.silabs.com/documents/public/data-sheets/efm8ub1-datasheet.pdf)

The owner subsequently reported continuity from the FW/reset button to the rightmost contact of
U4's upper edge, **pin 15, P1.7**. This GPIO could let the running firmware detect a button press and
request a software reset or bootloader entry. A subsequent unpowered continuity test between this pin
and a labelled GND terminal sounded with FW/reset pressed and stayed silent when released.
This confirms a button that grounds P1.7 when pressed. The firmware's response was subsequently
confirmed through USB enumeration. Holding FW/reset while connecting USB opens the vendor-defined
HID bootloader under the same 10C4:8B02 ID. It has no product or serial strings, uses usage page
0xFF00, and exposes 64-byte output and four-byte input payloads.

Silicon Labs' 8-bit Flash Programming Utilities support the USB Debug Adapter and provide a **Get Memory** operation. C2 permits reads of unlocked user and data pages. Record the ranges read, repeat the reads and compare their contents. A locked page prevents a complete C2 backup. Stop if the tool proposes **Device Erase**, which discards the installed contents. The protection state of this board is unknown. [AN136, section 2.2.3](https://www.silabs.com/documents/public/application-notes/AN136-production-programming-options.pdf) · [EFM8UB1 reference manual, table 4.3](https://www.silabs.com/documents/public/reference-manuals/efm8ub1-rm.pdf)

Heusinkveld lists DIView calibration software for the legacy Pro and Ultimate pedals. Its legacy downloads section does not list a restore image for this controller. [Heusinkveld downloads](https://heusinkveld.com/downloads/)

## Leo Bodnar handbrake controller

The second PCB is marked `BU0836-LC`. The clearer component photo provided on 2026-11-16 identifies
the main IC as **PIC18F2458-I/SO**, a 28-pin PIC with 24 KiB of program flash, 256 bytes of EEPROM
and a USB peripheral. Leo Bodnar specifies one dedicated load-cell input and seven other 12-bit
analogue inputs. Up to twelve buttons can be wired directly, or 32 through a matrix. The manufacturer
also offers OEM variants. [PIC18F2458 data sheet](https://ww1.microchip.com/downloads/en/DeviceDoc/39887c.pdf) · [BU0836-LC specifications](https://leobodnar.com/products/BU0836-LC/)

The product page publishes `BU0836LC.bin`. The copy retrieved on 2026-11-13 was **12,560 bytes**, SHA-256 `863d6044cd7cffbde11b478d40f8e7d64cea58c12080978b782d3012aae1fc45`. HID Flash uses a compatible running bootloader to install a vendor image. Compatibility with this unit's revision, equivalence to its installed firmware, and recovery after a full-chip erase are unverified. Record the board revision, USB product name, VID/PID and reported firmware version if available before comparing with the vendor. [Product downloads](https://leobodnar.com/shop/index.php?main_page=product_info&products_id=183) · [Manufacturer's binary](https://leobodnar.com/files/BU0836LC.bin) · [HID Flash instructions](https://www.leobodnar.com/shop/index.php?main_page=page&id=5)

The PIC18F2458 supports ICSP on its programming pins. MPLAB IPE supports **Read** and
**File → Export → Hex** for accessible memory through a compatible programmer. A successful file
export alone does not establish that every region was readable. The code-protection state of this
unit is unknown. [MPLAB IPE Read](https://onlinedocs.microchip.com/oxy/GUID-B611433A-00AE-481A-93FF-EE796652926C-en-US-5/GUID-E071857B-0152-43E4-B25E-A610312753DB.html) · [MPLAB IPE Export](https://onlinedocs.microchip.com/oxy/GUID-B611433A-00AE-481A-93FF-EE796652926C-en-US-5/GUID-ADF419B3-4EEE-4E60-96D6-E5C93D4BC240.html)

## USB inspection on 2026-11-16

Both stock devices were present. Windows HID capabilities were read through metadata handles.
The report sizes below include Windows' report-ID byte.

| Device | USB ID | Serial | bcdDevice | Input bytes | Output bytes | Feature bytes |
| --- | --- | --- | --- | --- | --- | --- |
| HE SIM PEDALS | 10C4:8B02 | (bench unit) | 0x0100 | 17 | 0 | 0 |
| BU0836-LC Interface | 16C0:05B7 | (bench unit) | 0x0121 | 8 | 0 | 18 |

The original pedal HID descriptor exposes seven axes and twelve buttons. Its original HID interface
declares no output or feature reports. The handbrake declares a vendor-defined feature report on page
0xFF00, report ID 0. Standard GET_FEATURE requests succeeded. Their contents varied between reads,
so the saved samples remain uninterpreted. This initial inspection preceded the pedal backup below.

The stock Silicon Labs USB bootloader protocol has Identify, Setup, Erase, Write, Verify, Lock and
RunApp commands. Its Verify command compares a supplied CRC16. It has no direct memory-read
command. The following backup investigation identified the installed bootloader and its P1.7 entry wiring. For the
factory EFM8UB1 QFN28 bootloader, the entry pin is P3.0/C2D (U4 pin 10), held low during reset.
The owner's FW/reset continuity measurement identifies P1.7 (U4 pin 15). A software-controlled entry
method would depend on the installed firmware. [AN945, sections 5.3.1 and 7.1](https://www.silabs.com/documents/public/application-notes/an945-efm8-factory-bootloader-user-guide.pdf)

An offline inspection of the vendor's older HIDFlash package found bootloader, file-selection and
flash controls. The published workflow covers sending a firmware file to the device. A USB firmware
export for this specific handbrake remains unverified. [Leo Bodnar HIDFlash instructions](https://www.leobodnar.com/shop/index.php?main_page=page&id=5)

### Pedal backup recovered over USB

The bootloader accepted Identify 0x3241 and returned version 0x90. Its CRC16 verification command
could be used to identify each individual flash byte by comparing the 256 possible one-byte CRCs.
The resulting backup covers all program flash at 0x0000–0x3FFF and all 1,024 bytes at
0xF800–0xFBFF. Every saved byte was confirmed again through a fresh USB handle, and full-region
CRC checks accepted the saved files and rejected deliberately altered copies. No erase, program or
lock command was issued.

| File | Bytes | SHA-256 |
| --- | --- | --- |
| program-0000-3fff.bin | 16,384 | 288c0b9a926ffc8c58abc4b20451d26461a6228fcaa03add05c86afd9eebb445 |
| nonvolatile-f800-fbff.bin | 1,024 | ce63f41254fe47809219539dfd5a8650c3c09f578f437bc7235a752a950aefaf |

Disassembly of the saved code confirms bootloader code at 0x3A00–0x3FFF and its entry routine at
0xFBC0. The routine checks P1.7. The boot signature at 0xFBFE is 0xA5. The code lock at 0xFBFF and
data lock at 0xFBBF are both 0x00. Keep the two security pages at 0xFB80–0xFBFF and the bootloader
code intact during an application-only USB update.

A later optional inspection of the factory read-only area at 0xFFC0–0xFFFF caused a USB disconnect.
The device re-enumerated normally as HE SIM PEDALS with serial (bench unit). That factory area was
not saved. The program and nonvolatile backups had already passed their independent checks.
Actual rewriting of the backup has not been tested. Repairing an erased or damaged bootloader
requires C2 access. [AN945, security section](https://www.silabs.com/documents/public/application-notes/an945-efm8-factory-bootloader-user-guide.pdf)

### Original pedal ADC configuration recovered offline

Disassembly of the verified program backup finds ADC initialization at `0x1ED4`:
`ADC0AC=0x82`, `ADC0TK=0xBF`, `ADC0PWR=0x4F`, and `REF0CN=0x00`.
Port setup at `0x1E79` sets `P0MDIN=0xFE`, making P0.0/VREF analog.
The acquisition routine at `0x1AC0` uses `ADC0CF=0x13` and channels `14-index`
for indices 0, 1 and 2. This independently agrees with LC1–LC3 continuity.
It selects the external VREF pin, ground pin, gain 1 and delayed tracking at
HFOSC0/3. `ADC0AC=0x82` accumulates two 12-bit results. The prototype averages
them to retain its existing raw range of 0–4092. These are software observations,
not a measurement of the reference voltage. The earlier prototype assumption
that this PCB used VDD as its ADC reference was incorrect and has been removed.
[ADC0CF, ADC0AC and REF0CN register definitions](https://www.silabs.com/documents/public/reference-manuals/efm8ub1-rm.pdf)

### Handbrake USB checks

HIDFlash 1.0 and the currently published HIDFlash 1.1 both recognized BU0836-LC Interface,
version 1.21, serial (bench unit). Both the equivalent vendor feature request and clicks on the Bootloader
buttons left the board in normal joystick mode. The owner also tested the 1.0 button manually
with the same result. Offline inspection confirmed that both versions send the same bootloader
entry feature request. A subsequent power cycle restored the usual identity and the lower
128-byte configuration snapshot matched the preceding sample.

GET_FEATURE report 0 cycles through sixteen addressed blocks of sixteen bytes. The lower 128 bytes
were stable across samples and a power cycle. Some higher blocks contain changing runtime state,
so these replies do not establish an EEPROM or program-flash dump. A configuration snapshot and
the manufacturer's standard binary are retained separately. The installed PIC firmware and its
bootloader have not been backed up.
