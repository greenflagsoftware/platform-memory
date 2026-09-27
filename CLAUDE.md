# AgentMemory — Project Instructions for Claude Code

## Available Tools

### on-demand: search_memories
Search past memories from this project's AgentMemory sidecar.

**Endpoint:** `POST http://localhost:5098/tools/search-memories`
**Payload (JSON, snake_case):**
```json
{
  "query": "What did we decide about the data pipeline?",
  "limit": 5,
  "min_similarity": 0.7,
  "category": null
}
```

**When to use:** Before making a significant decision, before asking about project conventions, or when you suspect relevant context might exist from a past session. This is explicitly an *on-demand* tool — call it when you need it, not on every turn.

### automatic: context injection
Relevant memories are automatically prepended to your prompt every time you submit one (via the `UserPromptSubmit` hook). You do not need to call `search_memories` for every turn — the injection is already there. Use the tool for deeper or more specific queries.

## Memory Categories
- `question` — user asked a question about the codebase, architecture, or process
- `coding` — agent was writing, editing, or debugging code
- `tool_call` — agent was exploring the codebase via Read/Grep/Glob/etc
- `decision` — design decision, preference, or rationale
- `configuration` — config, dependency, or environment setup
- `other` — none of the above