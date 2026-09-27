# Claude Code PostToolUse hook
# Fires after the agent uses a tool. Posts the tool-call details to the local
# AgentMemory server for capture and exits immediately (fire-and-forget).
#
# Environment variables (set in .claude/settings.json or shell):
#   CLAUDE_MEMORY_SERVER_URL - default http://localhost:5098
#   CLAUDE_SESSION_ID        - session identifier

$serverUrl = $env:CLAUDE_MEMORY_SERVER_URL
if (-not $serverUrl) { $serverUrl = "http://localhost:5098" }

$sessionId = $env:CLAUDE_SESSION_ID
if (-not $sessionId) { $sessionId = "unknown" }

$body = @{
    session_id  = $sessionId
    hook_event  = "PostToolUse"
    raw_content = $args -join " "
    metadata    = @{timestamp = (Get-Date -Format "o")} | ConvertTo-Json
} | ConvertTo-Json

try {
    Invoke-WebRequest -Uri "$serverUrl/capture/" -Method Post -Body $body -ContentType "application/json" -UseBasicParsing | Out-Null
} catch {
    # Silently ignore failures — must not block the agent
}