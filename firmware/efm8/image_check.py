"""Check an application-only Intel HEX image without opening a USB device.

The limits describe the backed-up HE SIM PEDALS EFM8UB10F16G board.
This checks file contents only. A programming host must also constrain every
erase/write operation and preserve the bootloader and both security pages.
"""

import argparse
import hashlib
import json
from pathlib import Path


APPLICATION_END = 0x3A00  # Exclusive. Bootloader code starts here.
NV_START, NV_END = 0xF800, 0xFB80
BUILD_LIMIT = 0x3800


def read_hex(path):
    return parse_hex(Path(path).read_text(encoding="ascii"))


def parse_hex(text):
    image = {}
    base = 0
    ended = False
    for line_number, line in enumerate(text.splitlines(), 1):
        line = line.strip()
        if not line:
            continue
        if ended:
            raise ValueError(f"line {line_number}: record after EOF")
        try:
            if not line.startswith(":") or any(c not in "0123456789abcdefABCDEF" for c in line[1:]):
                raise ValueError()
            record = bytes.fromhex(line[1:])
        except ValueError:
            raise ValueError(f"line {line_number}: invalid HEX record") from None
        if len(record) < 5 or len(record) != record[0] + 5 or sum(record) & 0xFF:
            raise ValueError(f"line {line_number}: invalid length or checksum")
        size, kind = record[0], record[3]
        offset = int.from_bytes(record[1:3], "big")
        data = record[4:-1]
        if kind == 0:
            if offset + size > 0x10000:
                raise ValueError(f"line {line_number}: data crosses a 64 KiB boundary")
            for i, value in enumerate(data, base + offset):
                if i in image:
                    raise ValueError(f"line {line_number}: duplicate address 0x{i:X}")
                image[i] = value
        elif kind == 1 and size == 0 and offset == 0:
            ended = True
        elif kind in (2, 4) and size == 2 and offset == 0:
            base = int.from_bytes(data, "big") << (4 if kind == 2 else 16)
        else:
            raise ValueError(f"line {line_number}: unsupported record type or shape {kind}")
    if not ended or not image:
        raise ValueError("missing EOF or empty image")
    return image


def check_application(image, *, allow_nv=False, code_limit=APPLICATION_END):
    if not 3 <= code_limit <= APPLICATION_END:
        raise ValueError("invalid application code limit")
    for address in sorted(image):
        if 0 <= address < code_limit:
            continue
        if allow_nv and NV_START <= address < NV_END:
            continue
        raise ValueError(f"forbidden address 0x{address:04X}: bootloader, security or non-application memory")
    if image.get(0) != 0x02 or 1 not in image or 2 not in image:
        raise ValueError("expected a complete 8051 LJMP reset vector at 0x0000")
    entry = image[1] * 256 + image[2]
    if entry < 3 or entry >= code_limit or entry not in image:
        raise ValueError("reset vector does not point into the application image")
    return {
        "check": "application-addresses-and-hex-integrity",
        "data_bytes": len(image),
        "code_end_exclusive": f"0x{max(a for a in image if a < code_limit) + 1:04X}",
        "nv_bytes": sum(NV_START <= a < NV_END for a in image),
        "preserved_ranges": ["0x3A00-0x3FFF", "0xFB80-0xFBFF"],
        "hardware_flash_tested": False,
    }


def inspect_file(path, *, allow_nv=False, code_limit=APPLICATION_END):
    path = Path(path)
    raw = path.read_bytes()
    result = check_application(parse_hex(raw.decode("ascii")), allow_nv=allow_nv, code_limit=code_limit)
    result["image"] = str(path.resolve())
    result["sha256_hex_file"] = hashlib.sha256(raw).hexdigest()
    return result


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("image", type=Path)
    parser.add_argument("--allow-nv", action="store_true", help="also permit backed-up user data at F800-FB7F")
    args = parser.parse_args()
    try:
        result = inspect_file(args.image, allow_nv=args.allow_nv)
    except (ValueError, OSError) as error:
        parser.exit(1, f"Image rejected: {error}\n")
    print(json.dumps(result, indent=2))


if __name__ == "__main__":
    main()
