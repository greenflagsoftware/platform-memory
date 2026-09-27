# AgentMemory Development Plan

Status: living document. Update as phases complete or the plan changes — this is not a
one-time artifact.

## What this project does

AgentMemory is a local sidecar service that gives coding-agent sessions (starting with
Claude Code) durable, cross-session memory. Project-local Claude Code hooks fire non-blocking
HTTP calls to an ASP.NET Core MCP server whenever the agent submits a prompt, calls a tool, or
ends a session. The server classifies each captured event with an LLM call (via OpenRouter) —
deciding whether it's worth remembering, and tagging it with a category (question, coding,
tool call, etc.) and a save-worthiness score — then embeds and stores anything that clears a
configurable threshold in PostgreSQL (pgvector). Retrieval (querying stored memories back into
a session) is out of scope for v0.1; this phase is capture-and-classify only.

## Scope

- In scope for v0.1:
  - ASP.NET Core MCP server (HTTP transport) that accepts capture events and returns
    immediately (202-style non-blocking response).
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
- Explicitly deferred:
  - Any retrieval path (search/query MCP tool, or a hook that injects past memories back into
    a session).
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
storage logic live **in the MCP server itself**, not behind a CLI subprocess call per request.
The CLI is a separate, thin client for manual/admin operations only.

```
src/AgentMemory/              ASP.NET Core MCP server (HTTP transport)
  Capture/                    Capture endpoint(s): accept event payload, return immediately,
                               continue classify+embed+store in a background Task
  Classification/             OpenRouter client, prompt construction, JSON response parsing,
                               threshold comparison
  Storage/                    EF Core (or Dapper) + pgvector access
  Program.cs                  MCP HTTP transport + /health wiring
src/AgentMemory.Cli/          Admin/manual entry point — not on the hot path
  Commands/                   e.g. query stored memories, re-classify a capture, apply
                               migrations
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
   threshold, only the raw capture is kept (or discarded, per the retention question below —
   flagged as an open question).

### Open questions to resolve during Phase 1

- Exact OpenRouter models to use for classification vs. embeddings (cost/latency/quality
  tradeoff), and whether one model can do both in a single call.
- Retention policy for events that don't clear the save threshold: keep the raw capture row
  for later re-classification/tuning, or discard it entirely.
- Threshold value(s) — likely need tuning per category rather than one global cutoff.

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
  (`POST /search/context`), and an MCP-style on-demand tool (`POST /tools/search-memories`).
  The `UserPromptSubmit` hook fetches relevant memories before each prompt and injects them
  as `<relevant_memories>` context. A `CLAUDE.md` file tells the agent about the on-demand
  tool and the automatic injection.
- Exit criteria: a `UserPromptSubmit` hook call prepends semantically related memories before
  the user prompt reaches Claude Code; an explicit `/tools/search-memories` call returns
  relevant results; all retrieval paths gracefully handle missing API keys and embedding
  failures (return empty, never throw).

## Reference

- Architecture loosely based on
  [greenflagsoftware/capability-module-template](https://github.com/greenflagsoftware/capability-module-template)
  (CLI-owns-logic / thin-MCP-transport shape, Dockerized sidecar, docker-compose for standalone
  local runs) — adapted here without the VTC-specific parts, and with classification/storage
  moved into the server itself for hook-path latency reasons (see Architecture above).
