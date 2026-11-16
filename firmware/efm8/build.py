"""Build the EFM8UB10F16G-QFN28 application using SDCC/MCS-51."""

import argparse
import hashlib
import json
import os
import re
from pathlib import Path
import shutil
import subprocess

from image_check import BUILD_LIMIT, inspect_file


# HE SIM PEDALS, U4 QFN28. LC1/U1 -> pin 16, LC2/U2 -> pin 17, LC3/U3 -> pin 18.
# The continuity measurements are documented in firmware/HARDWARE.md.
HE_SIM_PEDALS_MUX = (14, 13, 12)


def source_fingerprint():
    here = Path(__file__).resolve().parent
    digest = hashlib.sha256()
    for name in ("main.c", "usb.c", "settings.c", "efm8.h", "version.h", "../core/opc.c", "../core/opc.h", "build.py"):
        digest.update(name.encode("ascii") + b"\0")
        digest.update((here / name).read_text(encoding="utf-8").encode("utf-8"))
    return digest.hexdigest()


def number(value):
    return int(value, 0)


def main():
    here = Path(__file__).resolve().parent
    core = here.parent / "core"
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--sdcc", default="sdcc")
    parser.add_argument("--vid", type=number, required=True)
    parser.add_argument("--pid", type=number, required=True)
    parser.add_argument("--mux", type=number, nargs=3, metavar=("LC1", "LC2", "LC3"),
                        default=HE_SIM_PEDALS_MUX,
                        help="override ADC0MX channels in connector order (HE SIM PEDALS default: 14 13 12)")
    parser.add_argument("--out", type=Path, default=here / "bin")
    parser.add_argument("--diagnostic-no-feature", action="store_true",
                        help="bench-only original input descriptor, returns to bootloader after 10 seconds")
    args = parser.parse_args()
    if not all(1 <= value <= 0xFFFF for value in (args.vid, args.pid)):
        parser.error("USB VID and PID must be nonzero 16-bit numbers")
    external = set(range(1, 16)) | set(range(20, 24))
    if len(set(args.mux)) != 3 or not set(args.mux) <= external:
        parser.error("supply three different ADC0MX channels (1..15 or 20..23); P0.0 is reserved for VREF")
    out = args.out.resolve()
    out.mkdir(parents=True, exist_ok=True)
    sdcc = shutil.which(args.sdcc)
    if sdcc is None:
        parser.error("SDCC was not found. Install SDCC or specify the executable with --sdcc.")
    sdcc = Path(sdcc).resolve()
    # SDCC and its preprocessor invoke sibling tools (including cc1 on Windows).
    tool_env = dict(os.environ, PATH=str(sdcc.parent) + os.pathsep + os.environ.get("PATH", ""))
    command = [str(sdcc), "-mmcs51", "--std-c11", "--model-small", "--stack-auto",
               "--opt-code-size", "--xram-size", "1024", "--code-size", hex(BUILD_LIMIT)]
    defines = [f"-DOPC_USB_VID={args.vid}", f"-DOPC_USB_PID={args.pid}"]
    if args.diagnostic_no_feature:
        defines.append("-DOPC_DIAGNOSTIC")
    for i, channel in enumerate(args.mux, start=1):
        defines.append(f"-DOPC_LC{i}={channel}")
    objects = []
    for src in [here / "main.c", here / "usb.c", here / "settings.c", core / "opc.c"]:
        obj = src.with_suffix(".rel").name
        subprocess.run([*command, *defines, "-I", str(core), "-c", str(src), "-o", obj],
                       cwd=out, env=tool_env, check=True)
        objects.append(obj)
    subprocess.run([*command, *objects, "-o", "pedals.ihx"], cwd=out, env=tool_env, check=True)
    report = inspect_file(out / "pedals.ihx", code_limit=BUILD_LIMIT)
    if not args.diagnostic_no_feature:
        version_text = (here / "version.h").read_text(encoding="ascii")
        version = ".".join(re.search(r"#define OPC_VERSION_" + part + r"\s+(\d+)\b", version_text).group(1)
                           for part in ("MAJOR", "MINOR", "PATCH"))
        def setting(name):
            return int(re.search(r"#define OPC_" + name + r"\s+(\d+)\b", version_text).group(1))
        manifest = dict(schema=1, target="HE_SIM_PEDALS_EFM8UB10F16G_QFN28", version=version,
                        image="pedals.ihx", sha256=hashlib.sha256((out / "pedals.ihx").read_bytes()).hexdigest(),
                        profile_version=1, adc_bits=setting("ADC_BITS"), usb_interval_ms=setting("USB_INTERVAL_MS"), axes=3,
                        cpu_hz=setting("SYSCLK_HZ"), vid=args.vid, pid=args.pid, adc_mux=list(args.mux),
                        source_sha256=source_fingerprint())
        (out / "pedals.firmware.json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf-8")
    print(f"Image checked: {report['data_bytes']} bytes, code ends before {report['code_end_exclusive']}; bootloader and security pages excluded")
    print(f"LC1, LC2, LC3 ADC0MX: {', '.join(map(str, args.mux))}")
    print(out / "pedals.ihx")


if __name__ == "__main__":
    main()
