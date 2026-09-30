# Memory Quality Plan

Status: **fully implemented** (all phases shipped 2026-09-30).

Companion to [dev plan.md](dev%20plan.md); summary folded below.
Companion to [dev plan.md](dev%20plan.md); if phases here ship, fold a
summary into that document's phase list and keep this one as the detail record.

## Problem

An audit of the first 38 stored memories (2026-09-30) found that most are poor context for a
future session:

| Kind | Examples (memory ids) | Share of store | Value |
|---|---|---|---|
| Durable facts/decisions as full sentences | 1, 2, 3, 6, 7, 8 | ~15% | High |
| Raw `PostToolUse` logs (`Bash: {...} -> @{type=text; file=}`) | 10-14, 16, 18-20, 22-27, 30, 33-38 | ~55% | Near zero |
| Context-dependent fragments ("Yes, commit and push.") | 15, 21, 28, 29 | ~10% | Low / negative |
| Test/probe prompts from hook verification | 9, 17, 21, 24 | ~10% | Noise |
| Duplicates (pgvector/HNSW decision stored three times) | 1, 2, 3 | — | Wasteful |

The memory tool's own calls are captured too (ids 36-38 were created by the audit itself),
so the store feeds on its own retrieval.

### Root causes (verified in code)

1. **Threshold is on the wrong scale.** `Memory:SaveThreshold` defaults to `0.5`
   (`appsettings.json`, `CaptureProcessor.DefaultSaveThreshold`) but `save_worthiness` is
   0-4 (`ClassificationService` schema/prompt). Anything the model scores >= 0.5 is saved, so
   the threshold filters almost nothing. Scores of 1 for "Yes, commit and push." and for bare
   file reads confirm it.
2. **Every tool call is captured.** `.claude/settings.json` registers `PostToolUse` with
   `matcher: "*"`, and `hooks/PostToolUse.ps1` forwards `"$toolName: $input -> $result"`,
   where `$toolResult` is a PowerShell object stringified to `@{type=text; file=}`.
3. **The classifier sees one event with no context.** `ClassifyAsync(capture.RawContent)`
   gets only the text. A short reply can't be judged, and its stored `Content` can't be
   understood at retrieval time.
4. **Memory content is the raw capture.** `CaptureProcessor` stores `capture.RawContent`
   verbatim as `memories.content`; nothing rewrites it into a standalone fact.
5. **No deduplication.** `AddMemoryAsync` inserts unconditionally.
6. **No way to remove a bad memory.** `AdminEndpoints` has list and reclassify only.

## Goals and non-goals

Goals: the store holds standalone, durable facts; retrieval top-5 is not crowded by noise or
duplicates; existing data can be cleaned up without hand-editing SQL.

Non-goals: changing the embedding model, per-category thresholds, queue-backed processing
(all still deferred per the main plan).

## Success measures

Measured on a fixture set built from the current 38 rows (Phase 0) plus the live store:

- Noise rate (share of stored memories a reviewer would delete): from ~85% to under 20%.
- Duplicate clusters (pairs above 0.92 cosine similarity): zero on new writes.
- Every stored `content` is understandable with no other context (spot-check 20 rows).
- Capture path stays non-blocking; hook latency unchanged.

## Phases

Ordered cheapest and highest-leverage first. Each phase is independently shippable.

### Phase 0 — Baseline and fixtures

- Export the current `captures` + `memories` rows to `tests/AgentMemory.Tests/Fixtures/`
  (JSON), each hand-labelled `keep` / `drop` with a reason.
- Add a small evaluation test that runs a candidate gate/classifier over the fixtures and
  reports precision/recall of `keep`. Use it as the regression bar for phases 1-4.
- Exit criteria: fixtures committed; the current pipeline's noise rate recorded in this doc.

### Phase 1 — Stop capturing noise at the source

- **Tool-call allowlist in the hook.** Replace `matcher: "*"` with a matcher for
  state-changing / decision-bearing tools only (e.g. `Bash` restricted in the script to
  `git commit|push|merge`, `docker compose`, and package/config commands; `Edit|Write` on
  config/docs files). Reads, greps, globs, ToolSearch and `mcp__agent-memory__*` are never
  captured. Filtering happens in `PostToolUse.ps1` before the POST, so filtered events cost no
  server work.
- **Server-side denylist as a backstop.** In `CaptureEndpoints`, reject (still 202, no
  processing) captures whose `metadata.tool_name` is on a configurable
  `Memory:IgnoredTools` list, so a misconfigured hook can't refill the store.
- **Fix result stringification.** `$toolResult` is emitted as `@{...}`; serialize with
  `ConvertTo-Json -Depth 4 -Compress` and truncate to a fixed length (e.g. 500 chars) so
  results are readable when kept.
- **Minimum-content gate** (server, before the LLM call, in `CaptureProcessor`): skip
  prompts that are under N words with no code/identifier tokens, or match a
  confirmation/ack pattern (`yes`, `ok`, `go ahead`, `commit and push`, ...). Configurable
  under `Memory:Gate`. Skipped captures stay in `captures` (retention policy unchanged), so
  they can be reprocessed if the gate is tuned.
- Exit criteria: on the fixtures, >= 90% of `drop`-labelled tool calls and acks are rejected
  with zero `keep`-labelled decisions rejected; unit tests for the gate and the ignore list.

### Phase 2 — Fix the threshold and the classifier prompt

- **Correct the scale.** Change the default `Memory:SaveThreshold` to a value on the 0-4
  scale (start at `2.5`, tune against the Phase 0 fixtures) in `appsettings.json` and
  `CaptureProcessor.DefaultSaveThreshold`. Document the scale next to the setting. This is a
  behavior change: call it out in the changelog, and note existing memories are unaffected
  until Phase 5.
- **Rewrite the system prompt** in `ClassificationService` to score against "would this help a
  future session that has no memory of this one?", with explicit anchors and few-shot
  examples: 4 = architectural decision/convention/gotcha; 2 = useful how-to; 0 = command log,
  acknowledgement, test probe, retrieval of memories. Keep the JSON-schema response shape.
- **Constrain the schema**: `minimum: 0, maximum: 4` on `save_worthiness`, and add
  `reason` (short string, logged not stored) so tuning is debuggable.
- **Fix the error path.** `CaptureProcessor` comments "still record a low-score memory"
  on classification error but returns at the threshold check. Make the comment match reality
  and log the error as a distinct skip reason.
- Exit criteria: fixture precision/recall meets the Phase 0 bar; a score-distribution log
  line (or admin endpoint) shows scores spread across 0-4 rather than clustering high.

### Phase 3 — Deduplicate on write

- In `CaptureProcessor`, after embedding and before `AddMemoryAsync`, call
  `IMemoryRepository.SearchMemoriesAsync(embedding, limit: 1, minSimilarity: Memory:DedupSimilarity)`
  (default `0.92`, tuned on the fixtures — memories 1-3 must collapse).
- On a hit: don't insert. Update the existing row instead — bump `last_seen_at` and a
  `seen_count`, and keep the higher score. Requires a migration adding those two columns.
- Log `deduplicated` with both ids so threshold tuning is observable.
- Exit criteria: replaying the fixtures yields one pgvector/HNSW memory, not three; unit test
  with a fake repository covers hit, miss, and boundary similarity.

### Phase 4 — Distill captures into standalone facts (with context)

- Add a distillation step between the threshold check and embedding: a second (or merged)
  OpenRouter call rewrites the capture into one self-contained statement ("Decision: commit
  and push the hook fixes to origin/main"), given the surrounding context.
- **Context source.** Hooks already receive `transcript_path` on stdin. `UserPromptSubmit.ps1`
  and `Stop.ps1` include the previous 1-2 turns (bounded, ~1-2k chars) in `metadata`; the
  server passes them to the distiller but does not store or embed them.
- **What is stored.** `memories.content` becomes the distilled fact and is what gets
  embedded. `captures.raw_content` keeps the original. Add `memories.source_excerpt`
  (nullable) if traceability is wanted.
- The distiller may answer `NONE` when there is no durable fact; treat that as below
  threshold. This also lets Phase 2's scoring and this step share one call to keep latency and
  cost flat (decide when implementing; measure both).
- Exit criteria: spot-check of 20 new memories reads as standalone; fragments like
  "Yes, commit and push." either become a useful fact or are dropped; unit tests with a mocked
  OpenRouter for the `NONE` and normal paths.

### Phase 5 — Clean up existing data and admin tooling

- `DELETE /admin/memories/{id}` and `POST /admin/memories/prune` (filters: `category`,
  `max_score`, `ids`, `dry_run=true` default) — plus matching CLI commands
  (`delete-memory`, `prune`).
- `POST /admin/memories/reprocess`: for every capture, re-run the new gate, classifier,
  dedup and distiller, replacing the memory row when the outcome differs. Reuses the existing
  reclassify machinery; `dry_run` prints what would change.
- One-off: run `prune` on all `tool_call` memories and the fixtures labelled `drop`, then
  `reprocess` to rebuild the remainder.
- Exit criteria: after cleanup the audit query (list, category, score, content) shows no rows
  a reviewer would delete; a dry run is required before any destructive run.

### Phase 6 — Retrieval tuning and ongoing monitoring

- Verify what `UserPromptSubmit.ps1` sends for `/search/context` (limit, `min_similarity`)
  and raise the injection floor above the on-demand tool's 0.7 default, since injected
  noise costs on every prompt.
- Exclude `mcp__agent-memory__*` calls from capture (done in Phase 1) and confirm the
  injected `<relevant_memories>` block is not re-captured as part of the next prompt.
- Add a `GET /admin/stats` (counts by category, score histogram, dedupe count, gate skip
  reasons) and surface it on the dashboard so drift is visible.
- Exit criteria: stats endpoint live; a monthly re-audit of ~20 random memories is written
  into this doc.

## Risks

- **Over-filtering** discards a useful capture. Mitigation: everything stays in `captures`;
  `reprocess` recovers it after tuning.
- **Extra LLM cost/latency** from distillation. Mitigation: runs in the existing background
  task only for captures that already cleared the threshold; consider merging calls.
- **Threshold changes are a behavior change** for anyone relying on current saves. Mitigation:
  Phase 5 dry-run shows the delta before any destructive step.
- **Dedup false positives** merge distinct facts. Mitigation: conservative 0.92 start,
  logging both ids, and fixture tests with near-but-different decisions.
- **Distillation hallucinates** detail not in the capture. Mitigation: keep `raw_content`,
  instruct "restate only what is present", and spot-check.

## Open questions

- Which tools, beyond the initial allowlist, are worth capturing (e.g. `Edit` on `CLAUDE.md`)?
- Should `Stop` summarize the whole session rather than forward the last message?
- Merge scoring and distillation into one call, or keep two for debuggability?
- Do we want a manual "remember this" path (explicit user-flagged memories bypassing the
  score threshold)?
