"""Create a compact evidence receipt from real TRX and Playwright reports, without test payloads."""
import base64
from datetime import datetime, timezone
import hashlib
from io import BytesIO
import json
from pathlib import Path
import re
import subprocess
import sys
import xml.etree.ElementTree as ET
from zipfile import ZipFile

root = Path(__file__).resolve().parents[1]
evidence = root / "docs" / "entregable-parte-2" / "evidencias"
trx = root / ".local" / "parte2-test-results" / "parte2-regresion.trx"
tree = ET.parse(trx)
counters = tree.find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters")
if counters is None or int(counters.get("executed", "0")) == 0:
    sys.exit("The full .NET regression has no successful result; receipt not generated.")
resolved = []
retest_counters = None
retest_hash = None
binary_receipt = None
if int(counters.get("failed", "0")):
    failed = {item.get("testName") for item in tree.iter() if item.tag.endswith("UnitTestResult") and item.get("outcome") == "Failed"}
    expected = {"OpenSource1.SmokeTests.Api.ClientesVistasApiTests.AplicacionFechadaAntesDelPago_NoReduceLaFacturaHastaQueElPagoExiste"}
    if failed != expected or int(counters.get("failed")) != 1:
        sys.exit("Unexpected full-regression failures remain; do not reconcile them as a fixture-only correction.")
    retest = root / ".local" / "parte2-test-results" / "parte2-correccion-fixture.trx"
    retest_tree = ET.parse(retest)
    retest_counters = retest_tree.find(".//{http://microsoft.com/schemas/VisualStudio/TeamTest/2010}Counters")
    passed = {item.get("testName") for item in retest_tree.iter() if item.tag.endswith("UnitTestResult") and item.get("outcome") == "Passed"}
    if retest_counters is None or int(retest_counters.get("failed", "1")) or not failed.issubset(passed):
        sys.exit("The corrected historical-data fixture has not passed its retest.")
    subprocess.run([sys.executable, str(root / "scripts" / "parte2-binarios.py")], check=True)
    resolved = sorted(failed)
    retest_hash = hashlib.sha256(retest.read_bytes()).hexdigest()
    binary_receipt = json.loads((root / ".local" / "parte2-test-results" / "product-binarios.json").read_text())

def browser_stats(folder):
    html = (evidence / folder / "index.html").read_bytes()
    match = re.search(rb"data:application/zip;base64,([A-Za-z0-9+/=]+)", html)
    if not match:
        sys.exit("Missing embedded Playwright report.")
    with ZipFile(BytesIO(base64.b64decode(match.group(1)))) as archive:
        report = json.loads(archive.read("report.json"))
    stats = report["stats"]
    if stats.get("unexpected", 0) or stats.get("flaky", 0) or not stats.get("expected", 0):
        sys.exit("Browser report contains failures or no passed tests.")
    return stats

browser = browser_stats("reporte-e2e")
if browser.get("total", 0) < 2:
    sys.exit("Run the full browser regression, not only the Part 2 scenario.")

files = subprocess.check_output(["git", "ls-files", "-c", "-o", "--exclude-standard", "src", "tests", "scripts", ".github", "Dockerfile.api", "Dockerfile.blazor", "docker-compose.parte2.yml", "docker-compose.production.yml", "deploy", ".dockerignore"], cwd=root, text=True).splitlines()
fingerprint = hashlib.sha256()
for name in sorted(set(files)):
    path = root / name
    if path.suffix in {".cs", ".razor", ".csproj", ".ts", ".js", ".css", ".json", ".py", ".yml", ".cjs"} or path.name.startswith("Dockerfile") or path.name in {"Caddyfile", ".dockerignore"}:
        fingerprint.update(name.encode() + b"\0" + path.read_bytes())
receipt = {
    "generatedUtc": datetime.now(timezone.utc).isoformat(),
    "branch": subprocess.check_output(["git", "branch", "--show-current"], cwd=root, text=True).strip(),
    "baseline": "45bc4bc",
    "sourceAndTestsSha256": fingerprint.hexdigest(),
    "dotnetFullRun": {key: int(value) for key, value in counters.attrib.items()},
    "dotnetValidationMode": "full-suite-plus-fixture-only-retest" if resolved else "single-full-suite-pass",
    "dotnetFixtureRetest": {key: int(value) for key, value in retest_counters.attrib.items()} if retest_counters is not None else None,
    "resolvedFixtureFailures": resolved,
    "verifiedUniqueDotnetTests": int(counters.get("passed", "0")) + len(resolved),
    "unchangedProductAssemblies": binary_receipt,
    "fixtureRetestTrxSha256": retest_hash,
    "trxSha256": hashlib.sha256(trx.read_bytes()).hexdigest(),
    "playwrightRegression": browser,
    "playwrightApiOutage": browser_stats("reporte-api-caida"),
}
(evidence / "resultados.json").write_text(json.dumps(receipt, indent=2) + "\n")
print(json.dumps({"dotnetFullRun": receipt["dotnetFullRun"], "dotnetFixtureRetest": receipt["dotnetFixtureRetest"],
    "verifiedUniqueDotnetTests": receipt["verifiedUniqueDotnetTests"], "playwrightRegression": browser,
    "apiOutage": receipt["playwrightApiOutage"]}, indent=2))
