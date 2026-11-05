"""Run only the opt-in Part 2 scenario against its isolated lab; never print credentials."""
import os
from pathlib import Path
import subprocess
import sys
import time
import urllib.error
import urllib.request

root = Path(__file__).resolve().parents[1]
environment = os.environ.copy()
private_file = root / ".local" / "parte2.env"
if not private_file.exists():
    sys.exit("Run python3 scripts/parte2-env.py first.")
for line in private_file.read_text().splitlines():
    key, separator, value = line.partition("=")
    if separator and key in {"POSTGRES_PASSWORD", "JWT_SIGNING_KEY", "AUTH_SEED_DEFAULT_PASSWORD"}:
        environment[key] = value
environment.update({
    "E2E_PARTE2": "1", "E2E_BASE_URL": "http://localhost:18080",
    "E2E_API_URL": "http://localhost:18081", "E2E_PASSWORD": environment["AUTH_SEED_DEFAULT_PASSWORD"],
    "E2E_REPORT_DIR": "../../docs/entregable-parte-2/evidencias/reporte-e2e",
    "E2E_CAPTURAS": str(root / ".local" / "regression-screenshots"),
})
(root / ".local" / "regression-screenshots").mkdir(exist_ok=True)
if "--api-caida" not in sys.argv:
    deadline = time.monotonic() + 90
    for endpoint in ("http://localhost:18081/health/live", "http://localhost:18080/health/live"):
        while True:
            try:
                with urllib.request.urlopen(endpoint, timeout=3) as response:
                    if response.status == 200:
                        break
            except (urllib.error.URLError, TimeoutError):
                pass
            if time.monotonic() >= deadline:
                sys.exit("The dedicated Part 2 lab did not become healthy.")
            time.sleep(1)
command = [str(root / "tests/e2e/node_modules/.bin/playwright"), "test"]
if "--api-caida" in sys.argv:
    environment["E2E_API_CAIDA"] = "1"
    environment["E2E_REPORT_DIR"] = "../../docs/entregable-parte-2/evidencias/reporte-api-caida"
    command.extend(["specs/07-errores.spec.ts", "--grep", "@api-caida", "--output", "../../.local/parte2-api-caida"])
elif "--regression" not in sys.argv:
    command.append("specs/10-parte2.spec.ts")
sys.exit(subprocess.run(command, cwd=root / "tests/e2e", env=environment).returncode)
