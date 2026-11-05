"""Record/verify production assembly identity around a test-fixture-only correction."""
import hashlib
import json
from pathlib import Path
import sys

root = Path(__file__).resolve().parents[1]
assemblies = [f"OpenSource1.{name}" for name in ("Core", "Application", "Infrastructure", "Api", "Blazor")]
paths = [root / "src" / name / "bin" / "Release" / "net10.0" / f"{name}.dll" for name in assemblies]
paths += [root / "tests" / "OpenSource1.SmokeTests" / "bin" / "Release" / "net10.0" / f"{name}.dll" for name in assemblies]
current = {str(path.relative_to(root)): hashlib.sha256(path.read_bytes()).hexdigest() for path in paths}
receipt = root / ".local" / "parte2-test-results" / "product-binarios.json"
if "--registrar" in sys.argv:
    if receipt.exists():
        sys.exit("Existing binary receipt preserved. Do not overwrite verification history.")
    receipt.write_text(json.dumps(current, indent=2) + "\n")
    print("Recorded5production assemblies and their5test-runner copies.")
else:
    expected = json.loads(receipt.read_text())
    if current != expected:
        sys.exit("Production binaries changed; a new full regression is required.")
    print("Verified: all10production-assembly fingerprints are unchanged.")
