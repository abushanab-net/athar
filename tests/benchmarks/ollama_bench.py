"""T007 (G1-f): benchmark bge-m3 and extraction candidates on the local Ollama, print digests.

Usage: python tests/benchmarks/ollama_bench.py [--cpu-only] [--models qwen2.5:7b-instruct aya-expanse:8b]
Runs GPU (Ollama default) and CPU (num_gpu=0, like the CPU-only reference host) passes.
Stdlib only. Results go to stdout as a Markdown table to paste into docs/03 D8.
"""
import argparse, json, re, statistics, subprocess, time, urllib.request

OLLAMA = "http://localhost:11434"
EMBED_MODEL = "bge-m3"
TYPES = {"fact", "decision", "convention", "procedure", "policy", "preference", "incident", "glossary"}
SCOPES = {"personal", "team", "project", "company"}
ARABIC = re.compile(r"[؀-ۿ]")

# Short agent-session excerpts (FR-MM-06 input). `expect` = terms a good extraction should surface.
SESSIONS = [
    ("en-1", "en", """user: the deploy failed again because the migration ran before the db was up
assistant: I added a health check wait in the AppHost so web waits for postgres.
user: good. from now on we always use `dotnet ef migrations add`, never hand-written SQL. that's a team rule.
assistant: Noted. I also renamed the env var to ATHAR_DB_CONN.""", ["migrations", "sql"]),
    ("en-2", "en", """user: we decided to use MudBlazor instead of Fluent UI, licence is MIT and the community is bigger
assistant: OK, I'll replace the Fluent components on the settings page.
user: and I prefer tabs over spaces in razor files, personally.""", ["mudblazor", "tabs"]),
    ("ar-1", "ar", """المستخدم: قررنا أن تكون مدة الاحتفاظ بسجلات الجلسات ثلاثين يوماً فقط ثم تُحذف تلقائياً.
المساعد: تمام، سأضيف مهمة الحذف في العامل.
المستخدم: وأي رمز وصول شخصي ينتهي بعد تسعين يوماً افتراضياً، هذه سياسة الشركة.""", ["ثلاثين", "تسعين"]),
    ("ar-2", "ar", """المستخدم: الخادم توقف أمس لأن القرص امتلأ بملفات السجلات.
المساعد: أضفت تنبيهاً عندما تتجاوز مساحة القرص ٨٠٪ وحذفت السجلات القديمة.
المستخدم: ممتاز، اكتب الخطوات في دليل التشغيل حتى يتبعها الفريق.""", ["القرص", "تنبيه"]),
    ("mix-1", "mixed", """user: لازم كل PR يكون صغير ومربوط بمهمة واحدة من tasks.md
assistant: Got it, one PR per task against develop.
user: والـ reviewer لازم يكون الـ PM، هذا قرار المشروع.""", ["pr", "develop"]),
]

# qwen2.5 answers Arabic input in Chinese unless the language rule is in the system message.
SYSTEM = "You extract knowledge for a bilingual Arabic/English team. Always write titles and bodies in the transcript's language (Arabic stays Arabic). Never use Chinese."

PROMPT = """Extract durable knowledge worth remembering from this agent session transcript.
Return JSON: {{"candidates":[{{"type":one of {types},"scope":one of {scopes},"title":short,"body":one or two sentences}}]}}.
Write each candidate in the language the user used. Return at most 4 candidates. No commentary.

Transcript:
{transcript}"""

CHUNK = ("Athar keeps company memory, wiki pages and agent sessions in one Postgres database. "
         "يحفظ أثر ذاكرة الشركة وصفحات الويكي في قاعدة بيانات واحدة. ") * 6  # ~800 chars, mixed


def call(path, body, timeout=600):
    req = urllib.request.Request(OLLAMA + path, json.dumps(body).encode(), {"Content-Type": "application/json"})
    with urllib.request.urlopen(req, timeout=timeout) as r:
        return json.load(r)


def pct(xs, p):
    xs = sorted(xs)
    return xs[min(len(xs) - 1, round(p / 100 * (len(xs) - 1)))]


def unload(model):
    subprocess.run(["ollama", "stop", model], capture_output=True)  # else a loaded copy keeps its GPU/CPU placement


def bench_embed(opts, runs=20, batch=32):
    unload(EMBED_MODEL)
    call("/api/embed", {"model": EMBED_MODEL, "input": "warm up", "options": opts})
    lat = []
    for i in range(runs):
        t = time.perf_counter()
        r = call("/api/embed", {"model": EMBED_MODEL, "input": f"how do we run migrations {i}", "options": opts})
        lat.append((time.perf_counter() - t) * 1000)
    dim = len(r["embeddings"][0])
    t = time.perf_counter()
    call("/api/embed", {"model": EMBED_MODEL, "input": [CHUNK + str(i) for i in range(batch)], "options": opts})
    cps = batch / (time.perf_counter() - t)
    return {"dim": dim, "p50": statistics.median(lat), "p95": pct(lat, 95), "chunks_s": cps}


def bench_extract(model, opts):
    unload(model)
    call("/api/generate", {"model": model, "prompt": "hi", "stream": False, "options": {**opts, "num_predict": 1}})  # load
    rows = []
    for sid, lang, transcript, expect in SESSIONS:
        t = time.perf_counter()
        r = call("/api/chat", {"model": model, "stream": False, "format": "json",
                               "options": {**opts, "temperature": 0},
                               "messages": [{"role": "system", "content": SYSTEM},
                                            {"role": "user", "content": PROMPT.format(
                                   types=sorted(TYPES), scopes=sorted(SCOPES), transcript=transcript)}]})
        secs = time.perf_counter() - t
        tok_s = r["eval_count"] / (r["eval_duration"] / 1e9)
        try:
            cands = json.loads(r["message"]["content"])["candidates"]
            valid = bool(cands) and all(c.get("type") in TYPES and c.get("scope") in SCOPES for c in cands)
        except (ValueError, KeyError, TypeError, AttributeError):
            cands, valid = [], False
        text = " ".join(f"{c.get('title', '')} {c.get('body', '')}" for c in cands if isinstance(c, dict)).lower()
        recall = sum(term.lower() in text for term in expect) / len(expect)
        lang_ok = lang != "ar" or bool(ARABIC.search(text))
        rows.append({"id": sid, "secs": secs, "tok_s": tok_s, "valid": valid, "recall": recall, "lang_ok": lang_ok})
    return rows


def digests(models):
    tags = {m["name"]: m["digest"] for m in json.load(urllib.request.urlopen(OLLAMA + "/api/tags", timeout=30))["models"]}
    return {m: tags.get(m) or tags.get(m + ":latest") for m in models}


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--models", nargs="+", default=["qwen2.5:7b-instruct", "aya-expanse:8b"])
    ap.add_argument("--cpu-only", action="store_true")
    a = ap.parse_args()
    modes = [("cpu", {"num_gpu": 0})] if a.cpu_only else [("gpu", {}), ("cpu", {"num_gpu": 0})]

    print("## Digests\n| Model | Digest |\n|---|---|")
    for m, d in digests([EMBED_MODEL, *a.models]).items():
        print(f"| `{m}` | `sha256:{d}` |")

    print("\n## Embeddings (bge-m3)\n| Mode | Dim | Query p50 ms | Query p95 ms | Chunks/s (800 chars, batch 32) |\n|---|---|---|---|---|")
    for mode, opts in modes:
        e = bench_embed(opts)
        print(f"| {mode} | {e['dim']} | {e['p50']:.0f} | {e['p95']:.0f} | {e['chunks_s']:.1f} |")

    print("\n## Extraction (5 sessions: 2 en, 2 ar, 1 mixed)\n"
          "| Model | Mode | Mean s/session | Max s | Gen tok/s | Valid JSON+enums | Term recall | Arabic kept |\n|---|---|---|---|---|---|---|---|")
    for m in a.models:
        for mode, opts in modes:
            rows = bench_extract(m, opts)
            n = len(rows)
            print(f"| `{m}` | {mode} | {statistics.mean(r['secs'] for r in rows):.1f} | {max(r['secs'] for r in rows):.1f} "
                  f"| {statistics.mean(r['tok_s'] for r in rows):.1f} | {sum(r['valid'] for r in rows)}/{n} "
                  f"| {statistics.mean(r['recall'] for r in rows):.0%} | {sum(r['lang_ok'] for r in rows if r['id'].startswith('ar'))}/{sum(r['id'].startswith('ar') for r in rows)} |")
            for r in rows:
                print(f"<!-- {m} {mode} {r} -->")


if __name__ == "__main__":
    main()
