# Perf seed and the indicative load check (T081)

The formal NFR-01 run is on the reference host in Phase P (P5). This is the local, indicative version.

## Run it
```
dotnet run --project src/Athar.AppHost -- --Athar:Seed=perf        # ~15 min to seed, then web turns healthy
python tests/perf/loadcheck.py --insecure --threads 30 --think 2    # NFR-01-like: 30 people with pauses
python tests/perf/loadcheck.py --insecure --threads 16              # saturation: no pauses
```
`perf` is `qa` plus 10,000 pages (100 areas × 100, about 6 sections each), 5,000 memories across scopes, and 65k chunks with stored vectors. The vectors are deterministic pseudo-random unit vectors, because embedding 65k chunks with bge-m3 on CPU takes hours (D8). They give the vector index its real cost but no meaning. The text comes from a ~60-word English and Arabic vocabulary.

## Results, 2026-10-04 (dev machine: i9-14900HX, RTX 4090 laptop, Docker Desktop; perf seed)
Seeding took about 14 minutes: 10,021 pages, 65,060 chunks, 65,002 vectors, 5,042 memories.

| Scenario | Operation | Requests | p50 ms | p95 ms | NFR-01 p95 target |
|---|---|---|---|---|---|
| Single request, no load | search (common words / no match) | 5 each | 335 / 138 | — | < 800 |
| 30 users, think 2 s (12 req/s) | page open | 455 | 61 | 176 | < 1,500 ✔ |
| | search | 209 | 765 | **3,997** | < 800 ✘ |
| | recall | 74 | 495 | **4,146** | < 1,500 ✘ |
| 16 threads, no pauses (19 req/s) | page open | 718 | 59 | 104 | < 1,500 ✔ |
| | search | 323 | 2,113 | **2,854** | < 800 ✘ |
| | recall | 126 | 1,727 | **2,457** | < 1,500 ✘ |

No errors in any run. Edit propagation (collab) and the concurrent extraction/sync jobs in NFR-01 weren't part of this check.

## Reading it
- **Page open is far inside its target.** **Search and recall are fast on their own**, and their medians are fine with realistic pauses, but **their p95 under concurrent load misses**.
- **The synthetic corpus exaggerates FTS cost.** With a ~60-word vocabulary, every query term matches nearly all 65k chunks, and the FTS query ranks every match (`ts_rank` × 3 configurations) before its `LIMIT`. Real text matches far fewer chunks per term.
- **Costs that remained** were addressed in T092 (below): FTS ranking every match, and recall repeating both queries per project group.

- **Before the formal run (P5):** re-run this on the reference host (CPU-only Ollama, 16 vCPU) with a perf corpus of realistic vocabulary. The vector side can stay synthetic.

## T092, 2026-10-05 (same machine, perf seed, local bge-m3)
- **What changed:**
  - FTS ranks at most 500 chunks matching every term, plus 500 matching any term, instead of every match.
  - Recall ranks once for all its project groups.
- **Where the time went:** in one search with no load, the FTS query took 195 ms of about 250 ms in the database. The other two queries were fast: loading the visible page ids took 19 ms and the vector query 26 ms. With the cap, FTS takes 8–9 ms and the database total is about 25 ms. Most of what's left is the query embedding.

| Scenario | Operation | Before p50 / p95 ms | After p50 / p95 ms | NFR-01 p95 target |
|---|---|---|---|---|
| 30 users, think 2 s | page open | 34 / 60 | 30 / 60 | < 1,500 ✔ |
| | search | 429 / 976 | 154 / **221** | < 800 ✔ |
| | recall | 247 / 770 | 181 / **314** | < 1,500 ✔ |
| 16 threads, no pauses | requests in 60 s | 1,398 (23/s) | 2,840 (47/s) | — |
| | search | 1,705 / 2,508 | 760 / 1,012 | — |
| | recall | 1,354 / 2,089 | 800 / 1,074 | — |

The "Before" column was re-measured on develop right before the change; it's a little better than the T081 table above.

- **The visible set stays materialized.** It's one indexed query (6–19 ms), and the `ANY(...)` array keeps the vector query on the HNSW index.
- **Moving the access predicate into the vector SQL makes the plan unstable.** Postgres sometimes sorts all 65k vectors instead of using the index: 360–650 ms, against 13–40 ms when it uses the index. Discouraging the sort gets the index back, but it still costs 40–180 ms. So the predicate stays as an array.
- **Saturation is now the query embedding.** Ollama embeds one query at a time; the database isn't the limit.
- **Cap ceiling:** once more than 500 chunks match, which ones get ranked follows the scan order, not relevance. The vector list still ranks every visible chunk. If real corpora need better, a BM25 index (pg_search or RUM) ranks inside the index.

## Formal run (T100, AT-41)
On the pilot host with the DGX models. Everything here reads and writes **only** the perf project's own database.

**When:** preferably before the first owner is bootstrapped (deploy/README.md "Go-live" step 2), while the host has no real
data. Later works too, because the perf stack is a separate Compose project, but stop the real stack for the run window so
the two don't share the CPU. Tell the team first.

0. **Before you stop production:**
   - Announce it to every lane and the PM, with the expected duration and the **one** named owner of the restart.
   - Take a backup (`docker restart athar-backup-1`, then wait for "uploaded" in its log).
   - Only that owner restarts production.
1. **Start the perf stack beside the real one.** It uses the same `deploy/.env` (DGX key, images), its own volumes
   (`athar-perf_*`) and its own ports. Pangolin never points at it, and `deploy/perf.override.yml` turns its backups off.
   ```bash
   export ATHAR_LAN_PORT=18080 ATHAR_MCP_PORT=18081 ATHAR_SHARE_PORT=18082 ATHAR_WEB_PORT=18090
   export ATHAR_APP_URL=http://<server>:18080   # the editor's socket goes to this stack's /collab, not the real one
   P="docker compose -p athar-perf -f deploy/docker-compose.yml -f deploy/perf.override.yml --env-file deploy/.env"
   $P up -d && $P logs -f web    # seeding the perf profile takes ~11-15 min; web turns healthy after it
   ```
   The seeded vectors carry the active model id (#80), so search is hybrid from the start, with no re-embedding on the DGX.
2. **Run the load from another machine on the LAN.** Twenty headless browsers need about 7 cores, so they must not run on
   the server. Start both runs at the same time, 10 minutes each:
   ```bash
   # 30 browsing users, 2 agent recalls/s, one extraction job (+ --sync-source <id> if a source is set up)
   python tests/perf/loadcheck.py --url http://<server>:18080 --threads 30 --think 2 --agent-recalls 2 --extract --duration 600
   # 20 editors on 5 pages: edit propagation (NFR-01)
   ATHAR_PERF_URL=http://<server>:18080 ATHAR_PERF_EDITORS=20 ATHAR_PERF_PAGES=5 ATHAR_PERF_SECONDS=600 \
     dotnet test tests/Athar.IntegrationTests --filter FullyQualifiedName~EditLoadTests --logger "console;verbosity=detailed"
   ```
3. **NFR-02:** run the editor load again with `ATHAR_PERF_PAGES=1`: 20 editors on one page, with no edits lost.
4. **Record the tables here**, then remove the perf stack and its data: `$P down -v`.

The browsers and the load check sign in as the perf seed's qa users (the qa password and `athar_pat_qa_member`). Those
exist only in the perf database, never in the real one.

**Local trial of the editor load, 2026-10-05** (dev machine, qa seed). The browsers, Docker and a CPU-bound local Ollama all
shared one laptop, so these numbers only show that the tool works:

| Editors | Pages | Edits | Lost | p50 ms | p95 ms | max ms |
|---|---|---|---|---|---|---|
| 6 | 2 | 95 | 0 | 10 | 25 | 36 |
| 20 | 5 | 598 | 0 | 13 | 40 | 152 |
| 20 | 1 | 585 | 0 | 57 | 203 | 491 |

A first run of 20 editors on one page reached p95 690 ms. During it, the local Ollama was embedding on CPU at 17 cores and
the browsers used about 7, so it isn't a collab result. Collab and web together used under one core.

### First formal attempt, 2026-10-05 (DGX GB10, 20 cores; perf seed with Qwen3-Embedding-4B@1024 vectors; deployed f49901c)
| Run | Operation | Requests | p50 ms | p95 ms | Target p95 |
|---|---|---|---|---|---|
| API only (30 users, 2 agent recalls/s, 1 extraction) | page open | 5,282 | 25 | 37 | < 1,500 ✔ |
| | search | 2,568 | 124 | 160 | < 800 ✔ |
| | recall | 885 | 148 | 188 | < 1,500 ✔ |
| | agent recall | 1,198 | 148 | 185 | < 1,500 ✔ |
| Same, plus 20 editors on 5 pages | page open | 5,053 | 29 | 577 | < 1,500 ✔ |
| | search | 2,463 | 130 | **874** | < 800 ✘ |
| | recall | 851 | 159 | 905 | < 1,500 ✔ |
| | agent recall | 1,109 | 153 | 897 | < 1,500 ✔ |
| | edit propagation | 8,811 | 1,927 | **3,995** | < 300 ✘ |

- **The API side passes with a wide margin** while extraction runs on the DGX. The extraction job took about 1 s, and the DGX load stayed between 2 and 5 of 20 cores.
- **The failures come from the editors' Blazor circuits, not from search or collab.**
  - Web's memory grew from 3.5 GB to 16 GB in 9 minutes while editors were connected. With API load alone it stayed flat at about 300 MB.
  - Collab averaged 2% CPU.
  - On a fresh web, opening the perf space's wiki page threw `OutOfMemoryException` in the Blazor render diff at 2.2 GB.
- **Cause:** the wiki tree renders all 10,021 pages of the space fully expanded, each with its own action menu, in every circuit. Lane B is fixing it: a collapsed or lazy tree and one shared menu.
- **Next:** rerun both loads and NFR-02 after the fix. The perf stack is stopped with its data kept: `$P start`, with the exports above.

### Second and third formal attempts, 2026-10-05/06 (DGX; after #90, then #92)
| Run (20 editors on 5 pages + API load) | search p95 | recall p95 | agent recall p95 | edit p95 |
|---|---|---|---|---|
| With #90 (lazy wiki tree) | 2,092 ms ✘ | 2,044 ✘ | 2,179 ✘ | 20 ms ✔ |
| With #92 (reuse unchanged chunks) | 2,970 ✘ | 3,244 ✘ | 3,227 ✘ | 105 ✔ |

- **The database isn't the tail.** In a local repro, Postgres logged no statement over 250 ms and no lock waits.
- **The query embedding is.** On the model gateway it goes from p95 74 ms to 1.3–2.8 s during the run.
  - The gateway alone handles 15 single embeddings/s at p95 72 ms.
  - The embedding server's log showed bursts of 3,000–8,600 prompt tokens/s every ~30 s, with up to 45 requests waiting. That's collab's save cadence: each save re-indexed the edited page.
- **#92 couldn't help these pages.** The test's typing had grown each page's last paragraph into one block of 34–81 chunks. A block over 1,600 characters is cut into fixed-size pieces, so any insertion shifts every later piece, and the whole block was re-embedded on every save. That also made each run worse than the last.
- **The fix:**
  - A page re-indexes at most every 2 minutes while it's being edited (`Athar:Index:PageEvery`; tests use 0). The latest revision is always indexed.
  - The editor load now picks fresh perf pages each run, and its browsers rescan only the changed blocks.
- **NFR-02 on a fresh page:** 20 editors, p95 187 ms, 0 lost. In the formal NFR-02 run, edits were "lost" only because the test's own browsers fell behind rescanning a page bloated by earlier runs.

## AT-41 formal result: passed, 2026-10-06
DGX GB10 (20 cores), perf seed (10,021 pages, 65k chunks, 5,042 memories) with Qwen3-Embedding-4B@1024 vectors and the DGX
chat model, develop c970662. The load ran from a separate LAN machine. Production was paused for the window.

**NFR-01:** 30 browsing users, 2 agent recalls/s, one extraction job, and 20 editors on 5 pages, all together for 600 s.
| Operation | Requests | p50 ms | p95 ms | Target p95 | |
|---|---|---|---|---|---|
| page open | 5,243 | 25 | 38 | < 1,500 | ✔ |
| search | 2,549 | 129 | 177 | < 800 | ✔ |
| recall | 878 | 153 | 206 | < 1,500 | ✔ |
| agent recall | 1,197 | 153 | 205 | < 1,500 | ✔ |
| edit propagation | 17,784 deliveries, 0 lost | 12 | 25 | < 300 | ✔ |

0 errors in 9,867 API requests. On the DGX: web CPU averaged 33% (peak 77%) and web memory peaked at 802 MiB; Postgres CPU
averaged 40% (peak 76%); the load average ranged 0.7–3.2. Query embeddings on the gateway stayed at p95 95 ms over the run.
The embedding server had requests waiting in 1 of 65 samples (14 waiting during one re-index).

**NFR-02:** 20 editors on one page for 300 s: 54,929 deliveries, **0 lost**, p50 32 ms, **p95 287 ms** (target < 300) ✔.

**Watch items (not failures):**
- **NFR-02's margin is thin.** Per-minute p95 was 60, 61, 61, 98, then **534 ms** in minute 5, while the server stayed
  nearly idle (web 10–19%, collab 4–6%). The likely cause is the load machine's 20 browser tabs rendering one shared
  paragraph that grows with every marker, but that isn't confirmed. A real page doesn't take 20 people typing nonstop into
  one paragraph, so this is mostly a test artefact. Rerun with a longer window if it matters.
- **Occasional slow query embeddings** (0.4–2.4 s, about one every 2 minutes) when a throttled page re-index lands. The
  window p95 stays under about 106 ms.
- **Web memory** ended at 974 MiB after both runs and wasn't returned. That's normal .NET behaviour for a heap with no
  memory limit set, but worth watching in production.
