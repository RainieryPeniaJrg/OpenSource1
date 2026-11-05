"""Collect successful browser artifacts under stable names for the delivery."""
from pathlib import Path
import json
import shutil
import sys

root = Path(__file__).resolve().parents[1]
results = root / "tests" / "e2e" / "test-results"
status = json.loads((results / ".last-run.json").read_text())
if status.get("status") != "passed":
    sys.exit("Latest browser run did not pass; refusing to publish its artifacts.")
directories = list(results.glob("10-parte2-*chromium"))
if len(directories) != 1:
    sys.exit("Expected exactly one Part 2 scenario directory.")
source = directories[0]
destination = root / "docs" / "entregable-parte-2" / "evidencias"
for name in ["factura.png"] + [f"{kind}.{extension}" for kind in ("ventas", "inventario", "top-productos", "cuentas-por-cobrar") for extension in ("png", "pdf", "xlsx")]:
    artifact = source / name
    if not artifact.is_file():
        sys.exit(f"Missing required evidence: {name}")
    folder = destination / ("capturas" if artifact.suffix == ".png" else "exportaciones")
    folder.mkdir(parents=True, exist_ok=True)
    shutil.copyfile(artifact, folder / name)
print("Collected5screenshots and8report downloads from the successful run.")
