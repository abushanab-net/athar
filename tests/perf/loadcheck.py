"""T081: an indicative local load check against NFR-01 (the formal run on the reference host is Phase P, P5).

Start Athar with the perf seed, then run this against it:
    dotnet run --project src/Athar.AppHost -- --Athar:Seed=perf
    python tests/perf/loadcheck.py --insecure            # https://localhost:7100, the qa member's PAT

Each worker thread loops over a browsing mix (60% page open, 30% search, 10% recall) for --duration seconds, after a
warm-up. Prints p50/p95 per operation against the NFR-01 targets. Stdlib only. --insecure skips TLS verification and
is meant for the local dev certificate only.
"""
import argparse, json, random, ssl, statistics, threading, time, urllib.error, urllib.parse, urllib.request

TARGETS_MS = {"page_open": 1500, "search": 800, "recall": 1500, "agent_recall": 1500}  # NFR-01 p95 (recall: cold target)
WORDS = ["deploy", "rollback", "release", "incident", "runbook", "database", "migration", "postgres", "backup", "token",
         "approval", "staging", "monitoring", "escalation", "النشر", "الحادثة", "المراجعة", "التصعيد"]


def page_id(root, child):  # PerfSeed.Page(root, child): range 2, n = root * 1000 + child
    return f"00000000-0000-7000-8001-0002{root * 1000 + child:08x}"


class Client:
    def __init__(self, url, pat, insecure):
        self.url, self.pat = url.rstrip("/"), pat
        self.ctx = ssl._create_unverified_context() if insecure else None

    def request(self, method, path, body=None):
        req = urllib.request.Request(self.url + path, method=method, data=json.dumps(body).encode() if body is not None else b"",
                                     headers={"Authorization": f"Bearer {self.pat}", "Content-Type": "application/json"})
        with urllib.request.urlopen(req, context=self.ctx, timeout=30) as r:
            return r.read()

    def call(self, method, path, body=None):
        req = urllib.request.Request(self.url + path, method=method, data=json.dumps(body).encode() if body is not None else None,
                                     headers={"Authorization": f"Bearer {self.pat}", "Content-Type": "application/json"})
        t = time.perf_counter()
        with urllib.request.urlopen(req, context=self.ctx, timeout=30) as r:
            r.read()
        return (time.perf_counter() - t) * 1000


def operation(c, rng):
    roll = rng.random()
    if roll < 0.6:
        return "page_open", c.call("GET", f"/api/v1/pages/{page_id(rng.randrange(100), rng.randrange(100))}")
    query = " ".join(rng.sample(WORDS, 2))
    if roll < 0.9:
        return "search", c.call("GET", "/api/v1/search?" + urllib.parse.urlencode({"q": query}))
    return "recall", c.call("POST", "/api/v1/recall", {"query": query})


def worker(c, seed, until, results, errors, think=0.0):
    rng = random.Random(seed)
    while time.time() < until:
        try:
            op, ms = operation(c, rng)
            results.setdefault(op, []).append(ms)
        except Exception as e:  # counted, not fatal: an indicative run
            errors.append(repr(e)[:200])
        if think: time.sleep(rng.uniform(0, 2 * think))  # a person reading between clicks


def agents(c, rate, until, results, errors):
    """NFR-01's agent load: recalls at a fixed rate (open loop), each on its own thread, so a slow answer doesn't slow the rate."""
    rng, calls = random.Random(-1), []

    def one(query):
        try: results.setdefault("agent_recall", []).append(c.call("POST", "/api/v1/recall", {"query": query}))
        except Exception as e: errors.append(repr(e)[:200])

    while time.time() < until:
        t = threading.Thread(target=one, args=(" ".join(rng.sample(WORDS, 2)),)); t.start(); calls.append(t)
        time.sleep(1 / rate)
    for t in calls: t.join()


def background_jobs(c, extract, source):
    """NFR-01 runs one extraction job and one repo sync during the load: a captured session (as the hook commits it) and a sync."""
    import uuid
    if extract:
        start = json.loads(c.request("POST", "/api/v1/sessions", {"client": "claude-code", "external_session_id": str(uuid.uuid4()), "repo_remote": None}))
        records = ["we decided that production deploys need two approvals", "release notes go in the wiki under Releases",
                   "قررنا أن تكون مدة الاحتفاظ بالنسخ الاحتياطية خمسة وثلاثين يومًا"]
        c.request("POST", f"/api/v1/sessions/{start['session_id']}/commit", {"commit_id": str(uuid.uuid4()), "from_index": 0, "to_index": len(records) - 1,
                  "skipped": 0, "records": [{"index": i, "data": {"role": "user", "text": t}} for i, t in enumerate(records)]})
        print("Queued one extraction job (a 3-record session).")
    if source:
        c.request("POST", f"/api/v1/sources/{source}/sync")
        print(f"Started a sync of source {source}.")


def pct(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, round(p / 100 * (len(xs) - 1)))]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--url", default="https://localhost:7100")
    ap.add_argument("--pat", default="athar_pat_qa_member")
    ap.add_argument("--threads", type=int, default=16)
    ap.add_argument("--duration", type=int, default=60)
    ap.add_argument("--warmup", type=int, default=10)
    ap.add_argument("--think", type=float, default=0.0, help="mean seconds between a user's requests (0 = saturate)")
    ap.add_argument("--insecure", action="store_true")
    ap.add_argument("--agent-recalls", type=float, default=0.0, help="agent recalls per second on top of the users (NFR-01: 2)")
    ap.add_argument("--agent-pat", default=None, help="the agents' token (default: --pat)")
    ap.add_argument("--extract", action="store_true", help="queue one extraction job when the measured window starts (NFR-01)")
    ap.add_argument("--sync-source", default=None, help="a knowledge source id to sync when the window starts (NFR-01)")
    a = ap.parse_args()
    c = Client(a.url, a.pat, a.insecure)
    try:  # the page opens target perf pages; on the default qa seed every one would 404
        c.call("GET", f"/api/v1/pages/{page_id(0, 0)}")
    except urllib.error.HTTPError as e:
        if e.code == 404:
            raise SystemExit("The perf seed isn't loaded. Start Athar with: dotnet run --project src/Athar.AppHost -- --Athar:Seed=perf")
        raise

    warm = {}
    worker(c, 0, time.time() + a.warmup, warm, [])  # first requests open pools and load the vector index
    results, errors = {}, []
    background_jobs(c, a.extract, a.sync_source)
    until = time.time() + a.duration
    threads = [threading.Thread(target=worker, args=(c, i + 1, until, results, errors, a.think)) for i in range(a.threads)]
    if a.agent_recalls:
        threads.append(threading.Thread(target=agents, args=(Client(a.url, a.agent_pat or a.pat, a.insecure), a.agent_recalls, until, results, errors)))
    for t in threads: t.start()
    for t in threads: t.join()

    total = sum(len(v) for v in results.values())
    print(f"{a.threads} threads, think {a.think} s, {a.agent_recalls} agent recalls/s, {a.duration} s, {total} requests ({total / a.duration:.1f}/s), {len(errors)} errors\n")
    print("| Operation | Requests | p50 ms | p95 ms | NFR-01 p95 target | Result |\n|---|---|---|---|---|---|")
    for op in ("page_open", "search", "recall", "agent_recall"):
        if op == "agent_recall" and not a.agent_recalls: continue
        xs = results.get(op, [])
        if not xs:
            print(f"| {op} | 0 | – | – | {TARGETS_MS[op]} | no data |")
            continue
        p95 = pct(xs, 95)
        print(f"| {op} | {len(xs)} | {statistics.median(xs):.0f} | {p95:.0f} | < {TARGETS_MS[op]} | {'pass' if p95 < TARGETS_MS[op] else 'FAIL'} |")
    if errors:
        print("\nFirst errors:", *sorted(set(errors))[:5], sep="\n  ")


if __name__ == "__main__":
    main()
