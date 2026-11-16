This is OpenPedalControl's own firmware 1.13.0, embedded so users do not need to find or extract files.

Rebuilt from `firmware/efm8` with SDCC 4.5.0 #15242, ADC channels 14/13/12 and test USB ID 1209:0002.
HEX SHA-256: `ed64c2201542dcb76b32ef1c8ffcacd7fba81ffdd41994162e6b268f43a2c226`.
No manufacturer's firmware or personal backup is included.

1209:0002 and serial OPC-TEST0001 are private test identities, not unique project assignments.
They are unsuitable for redistributed, sold or manufactured hardware. This build supports the
verified bench board only. See `firmware/SPECS.md` for capabilities and remaining limitations.

When updating this payload, rebuild, verify the board/profile target and image hash, then copy
both the image and manifest here together. CI checks the payload checksum, version and source
fingerprint so firmware source changes require refreshing the included bundle.
