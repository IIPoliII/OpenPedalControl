"""AN945 protocol and guarded application update for the backed-up HE SIM PEDALS.

No USB access occurs on import. Lock (0x35) is deliberately unsupported.
Program flash pages are 512 bytes; nonvolatile data pages are 64 bytes.
"""

import binascii
from dataclasses import dataclass
import hashlib
from pathlib import Path

from image_check import APPLICATION_END, NV_START, NV_END, check_application, parse_hex


ACK, CRC_ERROR = 0x40, 0x43
MAX_WRITE = 59  # One command fits the 64-byte USB output payload.
PROGRAM_SHA256 = "288c0b9a926ffc8c58abc4b20451d26461a6228fcaa03add05c86afd9eebb445"
NV_SHA256 = "ce63f41254fe47809219539dfd5a8650c3c09f578f437bc7235a752a950aefaf"


class UpdateError(RuntimeError):
    pass


def crc16(data):
    return binascii.crc_hqx(data, 0)


def page_size(address):
    if 0 <= address < APPLICATION_END:
        return 512
    if NV_START <= address < NV_END:
        return 64
    raise ValueError(f"forbidden flash address 0x{address:04X}")


def check_write(address, length):
    size = page_size(address)
    if not 1 <= length <= MAX_WRITE or address // size != (address + length - 1) // size:
        raise ValueError("write must fit a single allowed flash page and USB report")
    page_size(address + length - 1)


def encode_report(command, data=b"", *, writable=False):
    if command in (0x32, 0x33):
        if not writable:
            raise ValueError("erase/write is disabled in this session")
        if len(data) < 2:
            raise ValueError("missing flash address")
        address = int.from_bytes(data[:2], "big")
        if command == 0x32:
            if len(data) != 2 or address % page_size(address):
                raise ValueError("erase must address an allowed page start and contain no data")
        else:
            check_write(address, len(data) - 2)
    else:
        lengths = {0x00: 0, 0x30: 2, 0x31: 3, 0x34: 6, 0x36: 2}
        if command not in lengths or len(data) != lengths[command]:
            raise ValueError("unsupported command or invalid payload length")
        if command == 0x30 and data != b"\x32\x41":
            raise ValueError("only the EFM8UB10F16G-QFN28 target is supported")
        if command == 0x31 and data != b"\xa5\xf1\x00":
            raise ValueError("invalid flash keys or bank")
        if command == 0x36 and data != b"\x00\x00":
            raise ValueError("unsupported restart option")
        if command == 0x34:
            start, end = int.from_bytes(data[:2], "big"), int.from_bytes(data[2:4], "big")
            if not (0 <= start <= end < 0x4000 or NV_START <= start <= end < 0xFC00):
                raise ValueError("CRC range includes reserved or factory memory")
    frame = bytes([0x24, len(data) + 1, command]) + data
    if len(frame) > 64:
        raise ValueError("command does not fit a USB report")
    return b"\x00" + frame + bytes(64 - len(frame))


class Protocol:
    def __init__(self, transport, *, writable=False, event=None):
        self.transport, self.writable = transport, writable
        self.event = event or (lambda **entry: None)
        self.failed = False

    def command(self, command, data=b"", *, expected=ACK):
        if self.failed:
            raise UpdateError("session stopped after an earlier protocol or I/O error; reconnect")
        report = encode_report(command, data, writable=self.writable)
        entry = dict(command=f"0x{command:02X}", payload_bytes=len(data))
        if command in (0x32, 0x33, 0x34):
            entry["address"] = f"0x{int.from_bytes(data[:2], 'big'):04X}"
        self.event(stage="command", **entry)
        try:
            reply = self.transport.exchange(report)
            if len(reply) != 5 or reply[0] != 0:
                raise UpdateError("malformed bootloader HID reply")
            status = reply[1]  # Remaining three bytes are unspecified, not zero padding.
            self.event(stage="response", status=f"0x{status:02X}", **entry)
            if status != expected:
                raise UpdateError(f"command 0x{command:02X}: expected 0x{expected:02X}, got 0x{status:02X}")
        except BaseException:
            self.failed = True
            raise

    def verify(self, address, data):
        if not data:
            raise ValueError("cannot verify an empty range")
        fields = address.to_bytes(2, "big") + (address + len(data) - 1).to_bytes(2, "big")
        self.command(0x34, fields + crc16(data).to_bytes(2, "big"))

    def erase(self, address):
        self.command(0x32, address.to_bytes(2, "big"))

    def write(self, address, data):
        self.command(0x33, address.to_bytes(2, "big") + data)


@dataclass(frozen=True)
class Backup:
    program: bytes
    nv: bytes

    def __post_init__(self):
        if len(self.program) != 0x4000 or len(self.nv) != 0x400:
            raise ValueError("incomplete program/nonvolatile backup")
        if self.nv[0x3FE:] != b"\xa5\x00" or self.nv[0x3BF] != 0:
            raise ValueError("unexpected boot signature or flash locks")

    @classmethod
    def load(cls, folder):
        folder = Path(folder)
        program = (folder / "program-0000-3fff.bin").read_bytes()
        nv = (folder / "nonvolatile-f800-fbff.bin").read_bytes()
        if hashlib.sha256(program).hexdigest() != PROGRAM_SHA256 or hashlib.sha256(nv).hexdigest() != NV_SHA256:
            raise ValueError("backup hashes do not match the verified bench-unit originals")
        return cls(program, nv)

    def check_target(self, protocol):
        protocol.command(0x30, b"\x32\x41")
        protocol.command(0x00, expected=0x90)
        protocol.command(0x31, b"\xa5\xf1\x00")  # Sets RAM state, no flash operation.
        self.check_preserved(protocol)

    def check_preserved(self, protocol):
        protocol.verify(APPLICATION_END, self.program[APPLICATION_END:])
        protocol.verify(NV_END, self.nv[NV_END - NV_START:])


@dataclass(frozen=True)
class Page:
    address: int
    data: bytes

    def __post_init__(self):
        size = page_size(self.address)
        if self.address % size or len(self.data) != size:
            raise ValueError("invalid flash page")


@dataclass(frozen=True)
class Plan:
    pages: tuple
    sha256: str

    @classmethod
    def load(cls, path, *, allow_nv=False):
        raw = Path(path).read_bytes()
        return cls.from_image(parse_hex(raw.decode("ascii")), hashlib.sha256(raw).hexdigest(), allow_nv=allow_nv)

    @classmethod
    def from_image(cls, image, sha256="simulation", *, allow_nv=False):
        check_application(image, allow_nv=allow_nv)
        pages = [Page(a, bytes(image.get(i, 0xFF) for i in range(a, a + 512)))
                 for a in range(0, APPLICATION_END, 512)]
        nv_pages = sorted({a // 64 * 64 for a in image if a >= NV_START})
        for a in nv_pages:
            if not all(i in image for i in range(a, a + 64)):
                raise ValueError("each requested nonvolatile page must be completely specified")
            pages.append(Page(a, bytes(image[i] for i in range(a, a + 64))))
        return cls(tuple(pages), sha256)

    def validate(self):
        if [p.address for p in self.pages] != sorted(p.address for p in self.pages):
            raise ValueError("pages must be in ascending order, starting with page zero")
        if [p.address for p in self.pages if p.address < APPLICATION_END] != list(range(0, APPLICATION_END, 512)):
            raise ValueError("plan must contain every application page in ascending order")
        if len({p.address for p in self.pages}) != len(self.pages):
            raise ValueError("duplicate flash pages")
        image = {}
        for p in self.pages:
            p.__post_init__()
            image.update(enumerate(p.data, p.address))
        check_application(image, allow_nv=True)

    def summary(self):
        self.validate()
        return dict(image_sha256=self.sha256, application_pages=APPLICATION_END // 512,
                    nv_pages=sum(p.address >= NV_START for p in self.pages),
                    erased_application_range="0x0000-0x39FF", unspecified_code_bytes="erased to 0xFF",
                    preserved_ranges=["0x3A00-0x3FFF", "0xFB80-0xFBFF"],
                    activation="reset-vector first byte written only after all checks",
                    hardware_flash_tested=False)


def verify_plan(protocol, backup, plan):
    plan.validate()
    backup.check_target(protocol)
    for page in plan.pages:
        protocol.verify(page.address, page.data)
    protocol.verify(0, b"".join(p.data for p in plan.pages if p.address < APPLICATION_END))
    backup.check_preserved(protocol)


def program_plan(protocol, backup, plan):
    """Stop on the first failure. Never retries a command or starts the application.

    Address zero stays erased until the complete new image and preserved areas
    pass CRC checks. A disconnect after activation requires manual boot entry.
    """
    plan.validate()
    if not protocol.writable:
        raise ValueError("programming requires an explicitly writable session")
    backup.check_target(protocol)
    protocol.event(stage="preflight-passed", **plan.summary())
    # Invalidate the old reset vector before changing any other page.
    protocol.erase(0)
    protocol.verify(0, bytes([0xFF]) * 512)
    for page in plan.pages:
        if page.address:
            protocol.erase(page.address)
        data = page.data if page.address else b"\xff" + page.data[1:]
        start = 0 if page.address else 1  # Defer the first reset-vector byte.
        for offset in range(start, len(data), MAX_WRITE):
            chunk = data[offset:offset + MAX_WRITE]
            if any(b != 0xFF for b in chunk):
                protocol.write(page.address + offset, chunk)
        protocol.verify(page.address, data)
        protocol.event(stage="page-verified", address=f"0x{page.address:04X}")
    code = b"".join(p.data for p in plan.pages if p.address < APPLICATION_END)
    protocol.verify(0, b"\xff" + code[1:])
    backup.check_preserved(protocol)
    protocol.event(stage="activating-reset-vector")
    protocol.write(0, plan.pages[0].data[:1])
    protocol.verify(0, plan.pages[0].data)
    protocol.verify(0, code)
    backup.check_preserved(protocol)
    protocol.event(stage="programming-verified", hardware_flash_tested=True)
