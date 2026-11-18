"""Fail if a tracked file names the owner's private infrastructure.

The repo is public. Design docs may describe "the home server" or "a deploy helper",
but never the identifiers that map the real setup: tailnet IPs and hostnames, LAN
addresses, server paths, systemd units or the deploy username.

Usage: python -I scripts/check-confidential.py   (run from the repo root; CI runs it on every push)
"""
import hashlib
import re
import subprocess
import sys

PATTERNS = {
    "tailnet IP": re.compile(r"\b100\.(6[4-9]|[7-9]\d|1[01]\d|12[0-7])\.\d+\.\d+"),
    "tailnet hostname": re.compile(r"\.ts\.net\b"),
    "LAN address": re.compile(r"\b192\.168\."),
    "server path": re.compile(r"/usr/local/(sbin|lib)|/srv/|/home/"),
    "systemd unit": re.compile(r"\b[\w@-]+\.service\b"),
}

# GitHub Actions expressions that merely look like unit names.
ALLOWED = re.compile(r"\bmatrix\.service\b")

# Private names are matched by SHA-256 so that this public file doesn't spell them out.
# Add one with: python -c "import hashlib;print(hashlib.sha256(b'<name>'.lower()).hexdigest())"
PRIVATE_NAME_HASHES = {
    "009a26c8daeda7858652465b618229c667551e6b068daa9599255a7e37a1f260",  # deploy username
}
TOKEN = re.compile(r"[A-Za-z0-9_.-]+")

SELF = "scripts/check-confidential.py"


def tracked_files():
    out = subprocess.run(["git", "ls-files", "-z"], capture_output=True, check=True).stdout
    return [f for f in out.decode().split("\0") if f and f != SELF]


def main():
    findings = []
    for path in tracked_files():
        try:
            with open(path, "rb") as fh:
                data = fh.read()
        except OSError:
            continue
        if b"\0" in data[:8192]:
            continue  # binary
        text = data.decode("utf-8", errors="replace")
        for lineno, line in enumerate(text.splitlines(), 1):
            scrubbed = ALLOWED.sub("", line)
            for label, rx in PATTERNS.items():
                if rx.search(scrubbed):
                    findings.append(f"{path}:{lineno}: {label}")
            for tok in TOKEN.findall(line):
                if hashlib.sha256(tok.lower().encode()).hexdigest() in PRIVATE_NAME_HASHES:
                    findings.append(f"{path}:{lineno}: private name")
    if findings:
        # Print locations only, never the matched text: CI logs are public too.
        print("Confidential infrastructure details found:", file=sys.stderr)
        print("\n".join(findings), file=sys.stderr)
        return 1
    print("No confidential infrastructure details found.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
