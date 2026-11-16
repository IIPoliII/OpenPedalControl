"""Exercise the actual update engine against an independent AN945 flash model."""

from pathlib import Path
import sys
import unittest

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / "efm8"))
from bootloader import Backup, Plan, Protocol, UpdateError, crc16, encode_report, program_plan, verify_plan
from hid_windows import is_bootloader


def fixture():
    # Synthetic flash only. No manufacturer's firmware is checked into the repo.
    program = bytes((i * 13 + 7) & 255 for i in range(0x4000))
    nv = bytearray((i * 7 + 19) & 255 for i in range(1024))
    nv[0x3BF] = 0
    nv[0x3FE:] = b"\xa5\x00"
    backup = Backup(program, bytes(nv))
    image = {0: 2, 1: 0, 2: 3, 3: 0x22, **{i: (i * 19) & 255 for i in range(40, 1600)}}
    return backup, image


class FlashModel:
    def __init__(self, backup, *, fail_at=None, apply_then_fail=False, bad_status=None):
        self.memory = bytearray([255]) * 0x10000
        self.memory[:0x4000], self.memory[0xF800:0xFC00] = backup.program, backup.nv
        self.calls, self.writes, self.erases = [], [], []
        self.fail_at, self.apply_then_fail = fail_at, apply_then_fail
        self.bad_status = bad_status or {}
        self.setup = False
        self.written = set()

    def exchange(self, report):
        assert len(report) == 65 and report[:2] == b"\x00\x24"
        length, command = report[2:4]
        data = report[4:3 + length]
        index = len(self.calls)
        self.calls.append((command, data))
        if index == self.fail_at and not self.apply_then_fail:
            raise TimeoutError("injected disconnect before command")
        status = self.bad_status.get(command)
        if status is None:
            status = self.execute(command, data)
        if index == self.fail_at:
            raise TimeoutError("injected lost acknowledgement")
        return bytes([0, status, 0, 8, 8])

    def execute(self, command, data):
        if command == 0:
            return 0x90
        if command == 0x30:
            return 0x40 if data == b"\x32\x41" else 0x42
        if command == 0x31:
            self.setup = data == b"\xa5\xf1\x00"
            return 0x40
        if not self.setup:
            raise AssertionError("missing setup")
        address = int.from_bytes(data[:2], "big")
        if command == 0x34:
            end = int.from_bytes(data[2:4], "big")
            return 0x40 if crc16(self.memory[address:end + 1]) == int.from_bytes(data[4:], "big") else 0x43
        if command == 0x32:
            size = 512 if address < 0x4000 else 64
            assert address % size == 0 and len(data) == 2
            assert address + size <= 0x3A00 or 0xF800 <= address < address + size <= 0xFB80
            self.memory[address:address + size] = bytes([255]) * size
            self.written.difference_update(range(address, address + size))
            self.erases.append(address)
            return 0x40
        if command == 0x33:
            chunk = data[2:]
            assert 1 <= len(chunk) <= 59
            for i, value in enumerate(chunk, address):
                assert 0 <= i < 0x3A00 or 0xF800 <= i < 0xFB80
                if value == 255:  # The observed bootloader skips 0xFF writes.
                    continue
                assert i not in self.written, "flash byte programmed twice without erase"
                self.memory[i] &= value
                self.written.add(i)
            self.writes.append((address, chunk))
            return 0x40
        raise AssertionError(f"unexpected command {command:#x}")


class BootloaderTests(unittest.TestCase):
    def test_frame_and_crc_known_vectors(self):
        self.assertEqual(crc16(b"123456789"), 0x31C3)
        self.assertEqual(encode_report(0x30, b"\x32\x41")[:6], b"\x00\x24\x03\x30\x32\x41")
        self.assertEqual(len(encode_report(0)), 65)

    def test_never_encode_lock_or_unsafe_operations(self):
        cases = [(0x35, b"\xa5\x00"), (0x32, b"\x00\x01"),
                 (0x32, b"\x3a\x00"), (0x32, b"\xfb\x80"),
                 (0x33, b"\xfb\x7f\x00\x00"), (0x33, b"\x01\xff\x00\x00"),
                 (0x33, b"\x00\x01" + bytes(60)), (0x33, b"\x00\x01"),
                 (0x31, b"\xa5\xf1\x01"), (0x34, b"\xff\xc0\xff\xff\x00\x00")]
        for command, data in cases:
            with self.subTest(command=command, data=data), self.assertRaises(ValueError):
                encode_report(command, data, writable=True)
        for command, data in ((0x32, b"\x00\x00"), (0x33, b"\x00\x00\x02")):
            with self.assertRaises(ValueError):
                encode_report(command, data)

    def test_new_application_and_activation_order(self):
        backup, image = fixture()
        plan = Plan.from_image(image)
        model = FlashModel(backup)
        events = []
        program_plan(Protocol(model, writable=True, event=lambda **event: events.append(event)), backup, plan)
        self.assertEqual(model.erases, list(range(0, 0x3A00, 512)))
        self.assertEqual(model.writes[-1], (0, b"\x02"))
        self.assertTrue(all(address != 0 for address, _ in model.writes[:-1]))
        self.assertEqual(model.memory[:0x3A00], b"".join(p.data for p in plan.pages))
        self.assertEqual(model.memory[0x3A00:0x4000], backup.program[0x3A00:])
        self.assertEqual(model.memory[0xF800:0xFC00], backup.nv)
        self.assertEqual(events[-1]["stage"], "programming-verified")
        self.assertFalse(any(command in (0x35, 0x36) for command, _ in model.calls))
        verify_plan(Protocol(model), backup, plan)

    def test_original_application_plus_nv_restoration(self):
        backup, _ = fixture()
        image = dict(enumerate(backup.program[:0x3A00]))
        image.update(enumerate(backup.nv[:0x380], 0xF800))
        image.update({0: 2, 1: 0, 2: 3})
        plan = Plan.from_image(image, allow_nv=True)
        model = FlashModel(backup)
        model.memory[0xF800:0xFB80] = bytes([0]) * 0x380
        program_plan(Protocol(model, writable=True), backup, plan)
        self.assertEqual(model.memory[0xF800:0xFC00], backup.nv)
        self.assertEqual(model.erases[-14:], list(range(0xF800, 0xFB80, 64)))

    def test_preflight_failure_makes_no_flash_changes(self):
        backup, image = fixture()
        for kind in ("code", "security", "id", "version"):
            model = FlashModel(backup, bad_status={0x30: 0x42} if kind == "id" else {0: 0x91} if kind == "version" else {})
            if kind in ("code", "security"):
                model.memory[0x3A10 if kind == "code" else 0xFB80] ^= 1
            before = bytes(model.memory)
            with self.subTest(kind=kind), self.assertRaises(UpdateError):
                program_plan(Protocol(model, writable=True), backup, Plan.from_image(image))
            self.assertEqual(bytes(model.memory), before)
            self.assertFalse(model.writes or model.erases)

    def test_disconnect_at_every_transfer_never_retries_or_touches_protected_pages(self):
        backup, image = fixture()
        plan = Plan.from_image(image)
        reference = FlashModel(backup)
        program_plan(Protocol(reference, writable=True), backup, plan)
        commit = next(i for i, (command, data) in enumerate(reference.calls) if command == 0x33 and data == b"\x00\x00\x02")
        for apply_then_fail in (False, True):
            for fail_at in range(len(reference.calls)):
                with self.subTest(fail_at=fail_at, apply_then_fail=apply_then_fail):
                    model = FlashModel(backup, fail_at=fail_at, apply_then_fail=apply_then_fail)
                    protocol = Protocol(model, writable=True)
                    with self.assertRaises(TimeoutError):
                        program_plan(protocol, backup, plan)
                    self.assertEqual(len(model.calls), fail_at + 1)
                    with self.assertRaises(UpdateError):
                        protocol.command(0)
                    self.assertEqual(model.memory[0x3A00:0x4000], backup.program[0x3A00:])
                    self.assertEqual(model.memory[0xF800:0xFC00], backup.nv)
                    if model.erases and (fail_at < commit or fail_at == commit and not apply_then_fail):
                        self.assertEqual(model.memory[0], 255)

    def test_corrupt_write_stops_before_activation(self):
        backup, image = fixture()
        model = FlashModel(backup)
        original = model.execute
        def corrupt(command, data):
            result = original(command, data)
            if command == 0x33:
                model.memory[20] ^= 1
            return result
        model.execute = corrupt
        with self.assertRaises(UpdateError):
            program_plan(Protocol(model, writable=True), backup, Plan.from_image(image))
        self.assertEqual(model.memory[0], 255)
        self.assertFalse(any(a == 0 for a, _ in model.writes))

    def test_incomplete_nv_page_and_out_of_order_plan_rejected(self):
        backup, image = fixture()
        with self.assertRaises(ValueError):
            Plan.from_image({**image, 0xF800: 0}, allow_nv=True)
        plan = Plan.from_image(image)
        model = FlashModel(backup)
        with self.assertRaises(ValueError):
            program_plan(Protocol(model, writable=True), backup, Plan(tuple(reversed(plan.pages)), plan.sha256))
        self.assertFalse(model.calls)

    def test_reject_malformed_response_and_do_not_retry(self):
        class Malformed:
            def exchange(self, _):
                return b"\x00\x40"
        protocol = Protocol(Malformed())
        with self.assertRaises(UpdateError):
            protocol.command(0x30, b"\x32\x41")
        with self.assertRaises(UpdateError):
            protocol.command(0)

    def test_normal_joystick_is_never_selected(self):
        boot = dict(usb_id="10C4:8B02", usage_page=0xFF00, usage=1, input_bytes=5, output_bytes=65, feature_bytes=0)
        self.assertTrue(is_bootloader(boot))
        for field, value in (("input_bytes", 17), ("output_bytes", 0), ("usage_page", 1), ("usb_id", "16C0:05B7")):
            self.assertFalse(is_bootloader({**boot, field: value}))


if __name__ == "__main__":
    unittest.main()
