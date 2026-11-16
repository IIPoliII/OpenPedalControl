"""Test the firmware core against the app's live calibration and curve code.

Requires Python 3, the .NET 10 SDK and a GCC/Clang-compatible C compiler.
From the repository root run: python firmware/tests/run.py
To select a compiler, pass its command: python firmware/tests/run.py zig cc
Build products are created in a temporary directory.
"""

import os
from pathlib import Path
import shutil
import subprocess
import sys
import tempfile


def main():
    if sys.argv[1:] in (["-h"], ["--help"]):
        print(__doc__)
        return
    compiler = sys.argv[1:]
    if not compiler:
        for name in ("cc", "clang", "gcc", "zig"):
            if shutil.which(name):
                compiler = [name, "cc"] if name == "zig" else [name]
                break
    if not compiler:
        raise SystemExit("Supply a compiler command, e.g. python firmware/tests/run.py zig cc")
    tests = Path(__file__).resolve().parent
    with tempfile.TemporaryDirectory(prefix="opc-firmware-tests-") as folder:
        build = Path(folder)
        library = "opc.dll" if os.name == "nt" else "libopc.so"
        if sys.platform == "darwin":
            library = "libopc.dylib"
        flags = ["-dynamiclib"] if sys.platform == "darwin" else ["-shared"]
        if os.name != "nt":
            flags.append("-fPIC")
        subprocess.run([*compiler, *flags, "-std=c99", "-Wall", "-Wextra", "-Werror",
                        str(tests.parent / "core" / "opc.c"), "-o", str(build / library)],
                       check=True, cwd=build)
        subprocess.run(["dotnet", "build", str(tests / "HostTests.csproj"), "--output", folder,
                        "--verbosity", "quiet", "-p:BaseIntermediateOutputPath=" + str(build / "obj") + "/"],
                       check=True, cwd=build)
        subprocess.run(["dotnet", str(build / "HostTests.dll")], check=True, cwd=build)


if __name__ == "__main__":
    main()
