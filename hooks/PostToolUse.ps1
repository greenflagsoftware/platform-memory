# Claude Code PostToolUse hook
# Fires after the agent uses a tool. Posts the tool-call details to the local
# AgentMemory server for capture and exits immediately (fire-and-forget).
#
# Implements Phase 1 of the Memory Quality Plan:
#   1a. Tool-call allowlist — only captures state-changing / decision-bearing tools
#   1b. Result stringification — ConvertTo-Json with truncation to 500 chars
#
# Claude Code invokes this hook with the event payload as JSON on STDIN, not
# as a command-line argument. The payload includes session_id, tool_name,
# tool_input, and the tool's result (seen as either "tool_result" or
# "tool_response" depending on version).
#
# Environment variables (set in .claude/settings.json or shell):
#   CLAUDE_MEMORY_SERVER_URL - default http://localhost:5098
#   CLAUDE_SESSION_ID        - session identifier override (falls back to the
#                              session_id in the stdin payload)

$serverUrl = $env:CLAUDE_MEMORY_SERVER_URL
if (-not $serverUrl) { $serverUrl = "http://localhost:5098" }

$stdin = [Console]::In.ReadToEnd()
$payload = $null
try { $payload = $stdin | ConvertFrom-Json } catch {}

$sessionId = $env:CLAUDE_SESSION_ID
if (-not $sessionId -and $payload) { $sessionId = $payload.session_id }
if (-not $sessionId) { $sessionId = "unknown" }

$toolName = $null
$toolInputJson = $null
$toolResult = $null
if ($payload) {
    $propNames = $payload.PSObject.Properties.Name
    if ($propNames -contains "tool_name") { $toolName = $payload.tool_name }
    if ($propNames -contains "tool_input") {
        try { $toolInputJson = $payload.tool_input | ConvertTo-Json -Depth 6 -Compress } catch {}
    }
    if ($propNames -contains "tool_result") { $toolResult = $payload.tool_result }
    elseif ($propNames -contains "tool_response") { $toolResult = $payload.tool_response }
}

# ── Phase 1a: Tool-call allowlist ─────────────────────────────
# Only capture state-changing / decision-bearing tools. Reads,
# greps, globs, ToolSearch, and AgentMemory's own MCP tools are
# skipped entirely — they produce no durable facts.
function Is-ToolAllowed {
    param([string]$name)
    if (-not $name) { return $false }

    # Always reject AgentMemory's own MCP tools (prevent feedback loop)
    if ($name -like "mcp__agent-memory__*") { return $false }

    # Reject known read-only / exploration tools
    $readOnlyTools = @(
        "Read",
        "Glob",
        "Grep",
        "ToolSearch",
        "Fetch",
        "WebSearch",
        "WebFetch",
        "Browse",
        "AskUser",
        "AskQuestion"
    )
    foreach ($ro in $readOnlyTools) {
        if ($name -eq $ro) { return $false }
    }

    # Reject all other MCP tools (third-party; noisy)
    if ($name -like "mcp__*") { return $false }

    # State-changing / decision-bearing tools
    $allowedTools = @(
        "Bash",        # filtered further below
        "Edit",
        "Write",
        "Create",
        "Delete",
        "Rename",
        "Move",
        "StrReplace",
        "ReadLints",   # lint results worth noting
        "TodoWrite"    # task tracking
    )
    foreach ($at in $allowedTools) {
        if ($name -eq $at) { return $true }
    }

    return $false
}

if (-not (Is-ToolAllowed $toolName)) {
    # Silently skip — filters out ~80% of noise at the hook level
    exit 0
}

# ── Phase 1a: Bash command content filtering ───────────────────
# For Bash tool calls, only capture state-changing commands
# (commits, pushes, merges, installs, deploys, etc.).
# Purely read-only commands (ls, cat, head, tail, etc.) are skipped.
if ($toolName -eq "Bash" -and $toolInputJson) {
    function Is-BashCommandAllowed {
        param([string]$inputJson)

        # Patterns for state-changing commands
        $changingPatterns = @(
            'git\s+(commit|push|merge|checkout\s+-b|tag|rebase|add\s|branch\s+-[dDmM])',
            'docker\s+(compose|build\s|push|login)',
            'npm\s+(install|init|publish|run\s+build|run\s+test)',
            'yarn\s+(add|init|publish|run)',
            'pnpm\s+(add|init|publish|run)',
            'dotnet\s+(build|publish|add\s+package|restore|format|test|run|clean)',
            'cargo\s+(build|publish|add|run|test)',
            'pip\s+install',
            'gem\s+install',
            'brew\s+(install|upgrade|tap)',
            'apt(-get)?\s+(install|update|upgrade)',
            'choco\s+install',
            'npx\s+(create|init)',
            'claude\s+mcp\s+add',
            'terraform\s+(apply|plan\s+-out|destroy|init)',
            'kubectl\s+(apply|create|delete)',
            'mkdir',
            'rm\s+-rf',
            'mv\s',
            'cp\s+-r',
            'systemctl\s+(start|stop|restart|enable|disable)',
            'service\s+(start|stop|restart)',
            'echo\s+.*>',
            'docker\s+exec'
        )

        foreach ($pattern in $changingPatterns) {
            if ($inputJson -match $pattern) { return $true }
        }

        return $false
    }

    if (-not (Is-BashCommandAllowed $toolInputJson)) {
        # Read-only Bash commands (ls, cat, head, git log, etc.) — skip
        exit 0
    }
}

# ── Phase 1b: Fix result stringification ──────────────────────
# Use ConvertTo-Json so results are readable when kept, and
# truncate to 500 chars to keep capture payloads manageable.
$toolResultJson = ""
if ($toolResult) {
    if ($toolResult -is [string]) {
        $toolResultJson = $toolResult
    } else {
        try { $toolResultJson = $toolResult | ConvertTo-Json -Depth 4 -Compress } catch {
            try { $toolResultJson = "$toolResult" } catch {}
        }
    }
    if ($toolResultJson.Length -gt 500) {
        $toolResultJson = $toolResultJson.Substring(0, 497) + "..."
    }
}

$rawContent = "${toolName}: ${toolInputJson}"
if ($toolResultJson) { $rawContent = "$rawContent -> $toolResultJson" }

$body = @{
    session_id  = $sessionId
    hook_event  = "PostToolUse"
    raw_content = $rawContent
    metadata    = @{
        timestamp = (Get-Date -Format "o")
        tool_name = $toolName
    } | ConvertTo-Json
} | ConvertTo-Json

try {
    Invoke-WebRequest -Uri "$serverUrl/capture/" -Method Post -Body $body -ContentType "application/json" -UseBasicParsing | Out-Null
} catch {
    # Silently ignore failures — must not block the agent
}

exit 0