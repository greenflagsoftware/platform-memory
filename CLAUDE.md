# AgentMemory — Project Instructions for Claude Code

> **Note:** AgentMemory now exposes `search_memories` as a real MCP tool over
> HTTP/SSE transport. Claude Code can auto-discover it when connected via
> `claude mcp add`. No `CLAUDE.md` instruction is required for discovery, though
> usage guidance below is retained for convenience.

## Connection

```sh
claude mcp add --transport http http://localhost:5098/mcp
```

## Available Tools

### on-demand: search_memories (auto-discovered via MCP)
Search past memories from this project's AgentMemory sidecar.

**When to use:** Before making a significant decision, before asking about project
conventions, or when you suspect relevant context might exist from a past session.
This is explicitly an *on-demand* tool — call it when you need it, not on every turn.

**Parameters:**
- `query` (required) — the search query
- `limit` (optional, default 5) — maximum number of results
- `min_similarity` (optional, default 0.7) — minimum cosine similarity 0-1
- `category` (optional) — filter by category

### automatic: context injection
Relevant memories are automatically prepended to your prompt every time you submit
one (via the `UserPromptSubmit` hook). You do not need to call `search_memories` for
every turn — the injection is already there. Use the tool for deeper or more specific
queries.

## Memory Categories
- `question` — user asked a question about the codebase, architecture, or process
- `coding` — agent was writing, editing, or debugging code
- `tool_call` — agent was exploring the codebase via Read/Grep/Glob/etc
- `decision` — design decision, preference, or rationale
- `configuration` — config, dependency, or environment setup
- `other` — none of the above