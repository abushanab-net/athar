"""Local stand-in for .github/workflows/ci.yml while the GitHub Actions budget is exhausted.

Runs the same steps as the ci job, in order, and stops at the first failure:
guards (conflict markers, hand-written SQL, audit append-only), npm build, dotnet build, integration tests
(a container-start failure reruns the suite once; otherwise only the failed tests rerun once, and the
PASSED line names them, so a flaky test stays visible). Paste the last line it prints into the PR body.

    python tools/verify.py            # everything
    python tools/verify.py --no-tests # guards and builds only (quick check while working)
"""
import os, pathlib, re, subprocess, sys, tempfile, time

ROOT = pathlib.Path(__file__).resolve().parent.parent
INFRA = re.compile(r"did not become healthy|FailedToStart|Error response from daemon")


def run(cmd, cwd=ROOT, capture=False):
    print(f"\n$ {cmd}", flush=True)
    if not capture:
        return subprocess.run(cmd, cwd=cwd, shell=True).returncode, ""
    p = subprocess.Popen(cmd, cwd=cwd, shell=True, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, encoding="utf-8", errors="replace")
    out = []
    for line in p.stdout:
        print(line, end="", flush=True)
        out.append(line)
    return p.wait(), "".join(out)


def git(*args):
    return subprocess.run(["git", *args], cwd=ROOT, capture_output=True, text=True, encoding="utf-8").stdout


def guards():
    errors = []
    if git("grep", "-nE", r"^(<<<<<<<|>>>>>>>)( |$)", "--", "."):
        errors.append("merge-conflict markers are committed")
    if git("grep", "-nE", r"\bSql\(", "--", "src/Athar.Data/Migrations"):
        errors.append("a migration calls migrationBuilder.Sql (AT-55)")
    if git("ls-files", "*.sql").strip():
        errors.append(".sql files are not allowed (AT-55)")
    audit = re.compile(r"(AuditEvents|Set<AuditEvent>)[^;]*?\.Execute(Update|Delete)", re.S)
    for p in (ROOT / "src").rglob("*.cs"):
        if p.relative_to(ROOT).as_posix() != "src/Athar.Data/AuditRetention.cs" and audit.search(p.read_text(encoding="utf-8")):
            errors.append(f"{p.relative_to(ROOT)}: audit events are append-only (FR-AU-04)")
    return errors


def main():
    # Redirected to a file on Windows, stdout is cp1252 and the first Arabic test line would crash the run.
    sys.stdout.reconfigure(encoding="utf-8", errors="replace")
    start = time.time()
    steps = []

    def fail(step):
        print(f"\nverify: FAILED at {step} ({git('rev-parse', '--short', 'HEAD').strip()})")
        sys.exit(1)

    errors = guards()
    for e in errors:
        print(f"guard: {e}")
    if errors:
        fail("guards")
    steps.append("guards")

    for d in ("src/editor", "src/collab"):
        if (ROOT / d / "package.json").exists() and run("npm ci && npm run build", cwd=ROOT / d)[0]:
            fail(f"npm build {d}")
    steps.append("npm build")

    if run("dotnet build")[0]:
        fail("dotnet build")
    steps.append("dotnet build")

    if "--no-tests" not in sys.argv:
        lock = test_lock()
        try:
            tests(steps, fail)
        finally:
            lock.unlink(missing_ok=True)

    sha = git("rev-parse", "--short", "HEAD").strip()
    dirty = " +uncommitted" if git("status", "--porcelain", "-uno").strip() else ""
    print(f"\nverify: PASSED {sha}{dirty} in {int(time.time() - start)}s: {', '.join(steps)}")


def test_lock():
    """All lanes share one machine (Docker, Ollama). Parallel suites starve Ollama and AT-40 reports degraded search,
    so the test step runs one lane at a time. ponytail: a lock file, stale after 45 min (a killed run)."""
    lock = pathlib.Path(tempfile.gettempdir()) / "athar-verify.lock"
    while True:
        try:
            fd = os.open(lock, os.O_CREAT | os.O_EXCL | os.O_WRONLY)
            os.write(fd, f"{ROOT.name} {os.getpid()}".encode())
            os.close(fd)
            return lock
        except FileExistsError:
            try:
                if time.time() - lock.stat().st_mtime > 45 * 60:
                    lock.unlink(missing_ok=True)
                    continue
                holder = lock.read_text()
            except OSError:
                continue
            print(f"verify: waiting for the integration tests of {holder}", flush=True)
            time.sleep(30)


def tests(steps, fail):
    code, out = run("dotnet test tests/Athar.IntegrationTests --no-build", capture=True)
    if code and INFRA.search(out):
        print("\nverify: container start failure (infrastructure), retrying the tests once")
        code, out = run("dotnet test tests/Athar.IntegrationTests --no-build", capture=True)
    summary = re.findall(r"(?:Passed!|Failed!).*", out)
    failed = sorted(set(re.findall(r"^\s+Failed (\S+) \[", out, re.M)))
    if code and failed:
        # ponytail: a test that fails twice fails verify; one that passes on rerun is named in the PASSED line.
        print(f"\nverify: rerunning {len(failed)} failed test(s) once")
        names = "|".join(f"FullyQualifiedName={n}" for n in failed)
        code, _ = run(f'dotnet test tests/Athar.IntegrationTests --no-build --filter "{names}"')
        if not code:
            summary.append(f"passed after rerun of {', '.join(n.rsplit('.', 1)[-1] for n in failed)}")
    if code:
        fail("integration tests")
    steps.append(f"tests ({summary[-1].strip() if summary else 'passed'})")


if __name__ == "__main__":
    main()
