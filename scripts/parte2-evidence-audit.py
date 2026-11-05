"""Reject accidental inclusion of this lab's real credentials in delivery artifacts."""
from pathlib import Path
import base64
from io import BytesIO
import re
import sys
from zipfile import is_zipfile, ZipFile

root = Path(__file__).resolve().parents[1]
environment = root / ".local" / "parte2.env"
secrets = [line.partition("=")[2].encode() for line in environment.read_text().splitlines() if "=" in line]
exposed = []
def contains_secret(payload):
    if any(value in payload for value in secrets if value):
        return True
    stream = BytesIO(payload)
    if is_zipfile(stream):
        with ZipFile(stream) as archive:
            if any(contains_secret(archive.read(name)) for name in archive.namelist() if not name.endswith("/")):
                return True
    for encoded in re.findall(rb"data:application/zip;base64,([A-Za-z0-9+/=]+)", payload):
        if contains_secret(base64.b64decode(encoded)):
            return True
    return False

for item in (root / "docs" / "entregable-parte-2").rglob("*"):
    if item.is_file() and contains_secret(item.read_bytes()):
        exposed.append(str(item.relative_to(root)))
if exposed:
    sys.exit("Credential content detected in delivery files: " + ", ".join(exposed))
print("Delivery audit passed: no private lab credentials in artifacts.")
