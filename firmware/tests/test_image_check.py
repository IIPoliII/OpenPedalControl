"""Regression checks for bootloader/security exclusion from application images."""

import importlib.util
from pathlib import Path
import tempfile
import unittest


spec = importlib.util.spec_from_file_location("image_check", Path(__file__).resolve().parents[1] / "efm8" / "image_check.py")
checker = importlib.util.module_from_spec(spec)
spec.loader.exec_module(checker)


def record(address, data, kind=0):
    raw = bytes([len(data), address >> 8, address & 255, kind]) + bytes(data)
    return ":" + (raw + bytes([-sum(raw) & 255])).hex().upper()


class ImageCheckTests(unittest.TestCase):
    def parse(self, lines):
        with tempfile.TemporaryDirectory() as folder:
            path = Path(folder) / "test.hex"
            path.write_text("\n".join(lines), encoding="ascii")
            return checker.read_hex(path)

    def test_application_and_nv_boundaries(self):
        image = {0: 2, 1: 0, 2: 3, 3: 0x22, 0x39FF: 0}
        self.assertEqual(checker.check_application(image)["code_end_exclusive"], "0x3A00")
        for address in (0xF800, 0xFB7F):
            with self.subTest(address=address):
                with self.assertRaises(ValueError):
                    checker.check_application({**image, address: 0})
                self.assertEqual(checker.check_application({**image, address: 0}, allow_nv=True)["nv_bytes"], 1)

    def test_bootloader_and_security_always_rejected(self):
        for address in (0x3A00, 0x3FFF, 0x4000, 0xF7FF, 0xFB80, 0xFBBF, 0xFBC0, 0xFBFE, 0xFBFF, 0xFFC0, 0x10000):
            with self.subTest(address=address), self.assertRaises(ValueError):
                checker.check_application({0: 2, 1: 0, 2: 3, 3: 0x22, address: 0}, allow_nv=True)

    def test_build_reserves_extra_code_margin(self):
        with self.assertRaises(ValueError):
            checker.check_application({0: 2, 1: 0, 2: 3, 3: 0x22, 0x3800: 0}, code_limit=checker.BUILD_LIMIT)

    def test_reset_must_reach_present_application_code(self):
        for image in ({}, {0: 0}, {0: 2, 1: 0}, {0: 2, 1: 0x3A, 2: 0}, {0: 2, 1: 0, 2: 3}):
            with self.subTest(image=image), self.assertRaises(ValueError):
                checker.check_application(image)

    def test_hex_and_extended_addresses(self):
        image = self.parse([record(0, [2, 0, 3, 0x22]), record(0, [], 1)])
        self.assertEqual(checker.check_application(image)["data_bytes"], 4)
        for kind, data in ((4, [0, 1]), (2, [0x10, 0])):
            with self.subTest(kind=kind):
                high = self.parse([record(0, [2, 0, 3, 0x22]), record(0, data, kind), record(0, [0]), record(0, [], 1)])
                self.assertIn(0x10000, high)
                with self.assertRaises(ValueError):
                    checker.check_application(high)

    def test_corrupt_truncated_duplicate_or_trailing_records(self):
        good, eof = record(0, [2, 0, 3, 0x22]), record(0, [], 1)
        for lines in ([good[:-2] + "00", eof], [good], [good, good, eof], [good, eof, good], [eof], [":xx", eof], [record(0xFFFF, [0, 0]), eof], [good, record(1, [], 1)]):
            with self.subTest(lines=lines), self.assertRaises(ValueError):
                self.parse(lines)


if __name__ == "__main__":
    unittest.main()
