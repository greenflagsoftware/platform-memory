# AgentMemory Development Plan

Status: living document. Update as phases complete or the plan changes — this is not a
one-time artifact. Phases 0-3 have shipped; this revision adds Phase 4 (real MCP transport),
which is complete.

## What this project does

AgentMemory is a local sidecar service that gives coding-agent sessions (starting with
Claude Code) durable, cross-session memory. Project-local Claude Code hooks fire non-blocking
HTTP calls to an ASP.NET Core server whenever the agent submits a prompt, calls a tool, or
ends a session. The server classifies each captured event with an LLM call (via OpenRouter) —
deciding whether it's worth remembering, and tagging it with a category (question, coding,
tool call, etc.) and a save-worthiness score — then embeds and stores anything that clears a
configurable threshold in PostgreSQL (pgvector). Retrieval now exists too (Phase 3): a
`UserPromptSubmit` hook injects relevant past memories as context, and an on-demand search
endpoint is documented in `CLAUDE.md` for the agent to call directly.

Note on naming: the server is a plain ASP.NET Core HTTP API, not an MCP server with JSON-RPC
transport — Claude Code cannot auto-discover its endpoints as MCP tools. Its `/tools/*`
endpoints are surfaced to the agent only through `CLAUDE.md`'s instructions. See "Known
limitations" below.

## Scope

- In scope for v0.1:
  - ASP.NET Core HTTP API that accepts capture events and returns immediately (202-style
    non-blocking response).
  - In-process async classification: one OpenRouter call per event that returns both a
    category label and a save-worthiness score (JSON-mode prompt, parsed by the server).
  - In-process embedding generation via OpenRouter for events that clear the threshold.
  - PostgreSQL + pgvector storage of raw captures and classified/embedded memories.
  - Project-local Claude Code hooks (PowerShell) for `UserPromptSubmit`, `PostToolUse`, and
    `Stop`, each posting to the local server and exiting without waiting on a response.
  - A companion CLI project for admin/manual use (inspecting stored memories, re-running
    classification, running migrations) — not on the hot request path.
  - `docker-compose.yml` running the server + a Postgres/pgvector service together for local
    dev; a single Dockerfile publishing server + CLI into one image.
- Explicitly deferred (shipped in Phase 3, see below — kept here for the v0.1 scope record):
  - ~~Any retrieval path (search/query tool, or a hook that injects past memories back into a
    session).~~
  - Hooks installed outside this project folder (global/system-wide hook registration).
  - Multi-agent or multi-project support — v0.1 assumes one project's worth of memory.
  - Queue-backed/durable background processing — v0.1 does classification+embedding as an
    in-process async `Task` per request; a crash mid-task drops that one capture.

## Architecture

Loosely modeled on greenflagsoftware's `capability-module-template` (CLI-owns-logic,
thin-MCP-transport shape, Dockerized sidecar, docker-compose for standalone local runs), but
without the VTC-specific parts (no `CapabilityModule` naming prefix, no `module.manifest.json`,
no entitlement-by-running-container concept — there is no VTC host here). One deviation from
the template: because this server sits on a latency-sensitive hook path, classification and
storage logic live **in the HTTP server itself**, not behind a CLI subprocess call per request.
The CLI is a separate, thin client for manual/admin operations only.

```
src/AgentMemory/              ASP.NET Core HTTP API (see "Known limitations" re: not real MCP)
  Capture/                    Capture, admin, search, and tool endpoints
  Classification/             OpenRouter client, prompt construction, JSON response parsing,
                               threshold comparison
  Embedding/                  OpenRouter embeddings client
  Processing/                 CaptureProcessor: classify -> threshold -> embed -> store
  Retrieval/                  Semantic search over stored memories
  Storage/                    EF Core + pgvector access, migrations
  Program.cs                  HTTP pipeline + /health wiring
src/AgentMemory.Cli/          Admin/manual entry point — not on the hot path
  Commands/                   list-memories, re-classify, migrate
  Program.cs                  Dispatches argv[0] to the matching command
tests/AgentMemory.Tests/      Unit tests for classification/threshold logic (mocked
                               OpenRouter), payload parsing, prompt construction
hooks/                        Project-local Claude Code hooks (PowerShell), registered only
                               in this project's .claude/settings.json — not installed
                               globally while testing
docs/dev plan.md              This document
Dockerfile                    Publishes server + CLI into one runtime image
docker-compose.yml            Server + Postgres/pgvector, for local dev
.env                           (gitignored) OpenRouter API key, Postgres connection string
```

### Request flow

1. A Claude Code hook (`UserPromptSubmit`, `PostToolUse`, or `Stop`) fires a PowerShell script
   that POSTs the event (prompt/tool-call text, hook type, timestamp, any available session
   metadata) to the local server, then exits immediately — it does not wait for a response.
2. The server's capture endpoint validates and accepts the payload, kicks off a background
   `Task` to handle classification+embedding+storage, and returns immediately.
3. In the background task: one OpenRouter call classifies the event (category + save-worthiness
   score via a JSON-mode prompt). If the score clears the configured threshold, a second
   OpenRouter call generates an embedding for the content, and both the raw capture and the
   classified/embedded memory are written to Postgres. If the score doesn't clear the
   threshold, only the raw capture is kept — see "Resolved decisions" below.

### Resolved decisions

These were open questions during Phase 1; closed during the post-implementation review.

- **OpenRouter models.** Classification uses `openai/gpt-4o-mini` (chat completions,
  `response_format: json_schema`, one call returns both category and save-worthiness score).
  Embeddings use `openai/text-embedding-3-small` (1536 dimensions, matching the `memories`
  table's `vector(1536)` column). Both are **configurable**, not hardcoded: set
  `OPENROUTER_CLASSIFICATION_MODEL` / `OPENROUTER_EMBEDDING_MODEL` (or the equivalent
  `OpenRouter:ClassificationModel` / `OpenRouter:EmbeddingModel` config keys) to change them.
  Switching the embedding model to one with a different output dimension requires a migration
  updating the `memories.embedding` column type to match.
- **Retention policy.** Below-threshold captures are kept in `captures` (never deleted), just
  never promoted to `memories`. This keeps the door open for re-classification after a
  threshold or model change (see `POST /admin/captures/{id}/reclassify` and the CLI's
  `re-classify` command) without having to re-run the hook.
- **Threshold scope.** v0.1 ships one configurable global save-worthiness threshold
  (`Memory:SaveThreshold` / `Memory__SaveThreshold`, default `0.5` on a 0-4 score scale).
  Per-category thresholds are deferred to a later phase — not implemented, and not currently
  on the Phase Plan below.

## Draft Postgres schema (pgvector)

Subject to change once real classification output shapes are known; a starting point for
Phase 1.

```sql
CREATE EXTENSION IF NOT EXISTS vector;

CREATE TABLE captures (
    id              BIGSERIAL PRIMARY KEY,
    session_id      TEXT NOT NULL,
    hook_event      TEXT NOT NULL,         -- 'UserPromptSubmit' | 'PostToolUse' | 'Stop'
    raw_content     TEXT NOT NULL,
    metadata        JSONB NOT NULL DEFAULT '{}',
    captured_at     TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE TABLE memories (
    id              BIGSERIAL PRIMARY KEY,
    capture_id      BIGINT NOT NULL REFERENCES captures(id),
    category        TEXT NOT NULL,         -- 'question' | 'coding' | 'tool_call' | ...
    score           REAL NOT NULL,         -- save-worthiness score from classification
    content         TEXT NOT NULL,         -- normalized text that was embedded
    embedding       VECTOR(1536) NOT NULL, -- dimension depends on chosen OpenRouter model
    created_at      TIMESTAMPTZ NOT NULL DEFAULT now()
);

CREATE INDEX memories_embedding_hnsw
    ON memories USING hnsw (embedding vector_cosine_ops);
```

## Phase Plan

### Phase 0 — Prove the loop

- Deliverable: `docker compose up` starts the server + Postgres/pgvector, `/health` returns
  healthy, and a placeholder capture endpoint accepts a POST and writes a row to `captures`
  with no classification yet.
- Also: a single project-local `UserPromptSubmit` hook (PowerShell) that POSTs to the
  placeholder endpoint and exits without waiting, registered only in this project's
  `.claude/settings.json`.
- Exit criteria: submitting a prompt in a Claude Code session in this project produces a new
  row in `captures` within the local Postgres instance, with no perceptible delay to the
  session.

### Phase 1 — Classification and storage

- Deliverable: OpenRouter integration for classification (category + save-worthiness score)
  and embeddings, threshold comparison, and the `memories` table write path, all running as an
  in-process background `Task` per capture.
- Add `PostToolUse` and `Stop` hooks alongside `UserPromptSubmit`.
- Add the CLI's first admin command (e.g. list recent memories, or re-run classification on a
  given capture id).
- Exit criteria: a real prompt that should be remembered ends up in `memories` with a category,
  score, and embedding; a low-value prompt does not (per the retention policy decided above).

### Phase 2 — Harden

- Error handling for OpenRouter failures/timeouts and Postgres unavailability — capture must
  never block or crash the hook even if classification fails downstream.
- Resource limits/timeouts on the OpenRouter calls.
- Unit test coverage for classification/threshold logic and payload parsing (mocked
  OpenRouter), per the testing bar agreed for v0.1.
- Exit criteria: the server fails predictably (logged, capture still recorded) rather than
  losing data silently or blocking the hook on external-service errors.

### Phase 3 — Retrieval

- Deliverable: semantic search endpoint (`POST /search`), context-injection endpoint
  (`POST /search/context`), and an on-demand tool endpoint (`POST /tools/search-memories`,
  surfaced to the agent via `CLAUDE.md` instructions rather than MCP auto-discovery — see
  "Known limitations"). The `UserPromptSubmit` hook fetches relevant memories before each
  prompt and injects them as `<relevant_memories>` context.
- Exit criteria: a `UserPromptSubmit` hook call prepends semantically related memories before
  the user prompt reaches Claude Code; an explicit `/tools/search-memories` call returns
  relevant results; all retrieval paths gracefully handle missing API keys and embedding
  failures (return empty, never throw).

### Phase 4 — Real MCP transport

- Deliverable: added the [`ModelContextProtocol.AspNetCore`](https://www.nuget.org/packages/ModelContextProtocol.AspNetCore)
  package (v2.2.0) and exposed `search_memories` as an `[McpServerTool]`-attributed method on
  `McpTools` over the HTTP/SSE transport at `/mcp`. Claude Code connects with
  `claude mcp add --transport http http://localhost:5098/mcp` for auto-discovery; no
  `CLAUDE.md` instruction is required for discovery (though usage guidance is retained).
- Scope note: this **adds** a transport, it doesn't replace the plain HTTP one. The
  `/capture`, `/admin/*`, and `/search/context` endpoints stay plain HTTP — hooks and the CLI
  aren't MCP clients and have no reason to become one. Only the agent-facing on-demand
  tool moved to MCP; the background capture pipeline and admin/CLI surface are unaffected.
- The old `/tools/search-memories` REST endpoint has been removed in favour of the
  auto-discovered MCP tool.
- Exit criteria: `claude mcp add --transport http http://localhost:5098/mcp` connects and
  lists `search_memories` as an auto-discovered tool.

### Post-implementation fixes

Gaps found in a post-implementation review, closed in this pass:

- **Dockerfile now publishes the CLI.** It previously published only the server. It now
  publishes both projects into the runtime image (server as the entrypoint, CLI under
  `/app/cli`, run via `docker compose exec server dotnet /app/cli/AgentMemory.Cli.dll <command>`).
- **CLI gained `re-classify <id>` and `migrate`.** Both were listed in this plan's Scope but
  missing. `re-classify` calls the new `POST /admin/captures/{id}/reclassify` endpoint;
  `migrate` calls the new `POST /admin/migrate` endpoint.
- **Real EF Core migrations replace `EnsureCreated`/`EnsureDeleted`.** The server previously
  dropped and recreated its schema on every dev-mode startup, which made "apply migrations"
  meaningless — there were none. An `InitialCreate` migration now exists
  (`src/AgentMemory/Migrations/`), and the server calls `Database.MigrateAsync()` on startup
  in every environment; `POST /admin/migrate` applies pending migrations on demand without a
  restart.
- **Classification actually calls OpenRouter now.** The original implementation sent a
  TypeSafe-shaped request (`state`/`questions` with `choice`/`score` primitives) to a
  `/v1/systemone` path that doesn't exist on OpenRouter — a leftover from when this project's
  interview considered routing Jev (TypeSafe's own model) through OpenRouter. Per the decision
  to use OpenRouter directly instead (see the Reference section), classification is now a
  standard OpenRouter chat-completions call with a JSON-schema `response_format`.
- **Fixed a scoped-service lifetime bug in the capture endpoint.** The background
  classify/embed/store `Task` was capturing the HTTP request's scoped `CaptureProcessor`
  directly; that scope (and its `AppDbContext`) can be disposed before the background task
  runs, risking an `ObjectDisposedException` under load. It now creates its own
  `IServiceScopeFactory`-backed scope inside the background task.
- **`Stop` hook no longer hardcodes `raw_content`.** It previously always sent `"Session
  ended"`. It now forwards whatever Claude Code passes as the hook's payload (matching the
  `PostToolUse` hook's existing convention), falling back to the placeholder only if nothing
  is passed.

## Known limitations

- **Hooks read `$args`, not Claude Code's documented stdin JSON contract.** Claude Code hooks
  are typically invoked with a JSON payload on stdin (session id, transcript path, etc.); the
  hooks here (`.claude/settings.json`) pass `"$input"` as a single command-line argument
  instead. This works for the current PowerShell scripts but hasn't been reconciled against
  Claude Code's actual hook invocation contract — worth verifying end-to-end (not just via the
  `/capture` payloads arriving correctly) before relying on it more heavily, e.g. for the
  `Stop` hook's transcript-based summaries.

## Reference

- Architecture loosely based on
  [greenflagsoftware/capability-module-template](https://github.com/greenflagsoftware/capability-module-template)
  (CLI-owns-logic / thin-MCP-transport shape, Dockerized sidecar, docker-compose for standalone
  local runs) — adapted here without the VTC-specific parts, and with classification/storage
  moved into the server itself for hook-path latency reasons (see Architecture above).
