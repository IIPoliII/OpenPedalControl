"""Prepare, check or program the backed-up HE SIM PEDALS over Windows USB HID.

list/plan/probe/verify never erase or program flash. Programming requires the
program subcommand, --execute and the reviewed HEX file's --sha256 value.
The BU0836-LC handbrake is not supported by this programmer.
"""

import argparse
from datetime import datetime, timezone
import json
import os
from pathlib import Path
import sys
import uuid

from bootloader import Backup, Plan, Protocol, program_plan, verify_plan


class Journal:
    def __init__(self, path):
        path.parent.mkdir(parents=True, exist_ok=True)
        self.file = path.open("x", encoding="utf-8", buffering=1)
        self.path = path

    def event(self, **entry):
        entry["utc"] = datetime.now(timezone.utc).isoformat()
        self.file.write(json.dumps(entry, ensure_ascii=False) + "\n")
        self.file.flush()
        os.fsync(self.file.fileno())
        if entry["stage"] not in ("command", "response"):
            print(json.dumps(entry, ensure_ascii=False), flush=True)

    def close(self):
        self.file.close()


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    commands = parser.add_subparsers(dest="command", required=True)
    commands.add_parser("list", help="read device metadata only")
    for name in ("plan", "probe", "verify", "program"):
        sub = commands.add_parser(name)
        if name != "probe":
            sub.add_argument("image", type=Path)
            sub.add_argument("--allow-nv", action="store_true", help="permit complete user-data pages F800-FB7F")
        if name != "plan":
            sub.add_argument("--backup-dir", type=Path, required=True)
            sub.add_argument("--log", type=Path, help="new JSONL journal, defaults to backup-dir/usb-sessions/")
        if name in ("verify", "program"):
            sub.add_argument("--run", action="store_true", help="restart into the application only after verification")
        if name == "program":
            sub.add_argument("--execute", action="store_true", help="authorize actual page erase/program commands")
            sub.add_argument("--sha256", required=True, help="exact HEX SHA256 obtained from plan")
    args = parser.parse_args()
    journal = None
    try:
        if args.command == "list":
            from hid_windows import enumerate_devices
            print(json.dumps(enumerate_devices(), indent=2, ensure_ascii=False))
            return
        plan = None if args.command == "probe" else Plan.load(args.image, allow_nv=args.allow_nv)
        if args.command == "plan":
            print(json.dumps(plan.summary(), indent=2))
            return
        # All file validation happens before opening the USB interface.
        backup = Backup.load(args.backup_dir)
        if args.command == "program":
            if not args.execute or args.sha256.lower() != plan.sha256:
                parser.error("program requires --execute and --sha256 matching the reviewed HEX image")
        stamp = datetime.now(timezone.utc).strftime("%Y%m%dT%H%M%SZ")
        log = args.log or args.backup_dir / "usb-sessions" / f"{stamp}-{args.command}-{uuid.uuid4().hex[:8]}.jsonl"
        journal = Journal(log)
        journal.event(stage="session-start", operation=args.command, image_sha256=plan.sha256 if plan else None)
        from hid_windows import HidTransport
        with HidTransport() as transport:
            journal.event(stage="bootloader-opened", device=transport.device)
            protocol = Protocol(transport, writable=args.command == "program", event=journal.event)
            if args.command == "probe":
                backup.check_target(protocol)
            elif args.command == "verify":
                verify_plan(protocol, backup, plan)
            else:
                program_plan(protocol, backup, plan)
            if getattr(args, "run", False):
                protocol.command(0x36, b"\x00\x00")
                journal.event(stage="restart-requested")
        journal.event(stage="session-complete", operation=args.command)
        print(f"Journal: {journal.path}")
    except (OSError, ValueError, RuntimeError, KeyboardInterrupt) as error:
        if journal:
            journal.event(stage="session-stopped", error=str(error) or "interrupted",
                          recovery="No automatic retry or restart. Reconnect holding FW/reset before a new session.")
        print(f"Stopped: {error or 'interrupted'}", file=sys.stderr)
        return 1
    finally:
        if journal:
            journal.close()
    return 0


if __name__ == "__main__":
    sys.exit(main())
