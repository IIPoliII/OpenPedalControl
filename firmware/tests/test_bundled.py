"""Keep the firmware shipped in the desktop executable aligned with its source."""
import hashlib
import json
from pathlib import Path
import re
import sys
import unittest

ROOT = Path(__file__).resolve().parents[2]
sys.path.insert(0, str(ROOT / "firmware" / "efm8"))
from build import source_fingerprint
from image_check import parse_hex, check_application, BUILD_LIMIT


class BundledFirmwareTests(unittest.TestCase):
    def test_embedded_firmware_matches_manifest_version_and_source(self):
        bundle = ROOT / "src" / "OpenPedalControl" / "Firmware" / "Bundled"
        manifest = json.loads((bundle / "pedals.firmware.json").read_text(encoding="utf-8"))
        raw = (bundle / manifest["image"]).read_bytes()
        self.assertEqual(hashlib.sha256(raw).hexdigest(), manifest["sha256"])
        self.assertEqual(source_fingerprint(), manifest["source_sha256"], "Rebuild and refresh the embedded firmware bundle after source changes")
        check_application(parse_hex(raw.decode("ascii")), code_limit=BUILD_LIMIT)
        header = (ROOT / "firmware" / "efm8" / "version.h").read_text(encoding="ascii")
        version = ".".join(re.search(r"#define OPC_VERSION_" + p + r"\s+(\d+)\b", header).group(1)
                           for p in ("MAJOR", "MINOR", "PATCH"))
        self.assertEqual(version, manifest["version"])
        self.assertEqual(12, manifest["adc_bits"])
        self.assertEqual(4, manifest["usb_interval_ms"])
        self.assertEqual([14, 13, 12], manifest["adc_mux"])


if __name__ == "__main__":
    unittest.main()
