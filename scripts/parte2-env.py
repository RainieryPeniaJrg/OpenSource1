"""Create private, untracked demo credentials without printing them."""
import os
from pathlib import Path
import secrets

target = Path(__file__).resolve().parents[1] / ".local" / "parte2.env"
target.parent.mkdir(exist_ok=True, mode=0o700)
if target.exists():
    print("Existing private environment preserved:", target)
else:
    descriptor = os.open(target, os.O_WRONLY | os.O_CREAT | os.O_EXCL, 0o600)
    with os.fdopen(descriptor, "w") as output:
        output.write(f"POSTGRES_PASSWORD={secrets.token_hex(24)}\n")
        output.write(f"JWT_SIGNING_KEY={secrets.token_hex(48)}\n")
        output.write(f"AUTH_SEED_DEFAULT_PASSWORD=Aa1{secrets.token_hex(20)}\n")
    print("Private demo environment created:", target)
