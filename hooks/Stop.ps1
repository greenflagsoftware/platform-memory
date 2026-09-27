# Claude Code Stop hook
# Fires when a Claude Code session ends. Posts session metadata to the local
# AgentMemory server for capture and exits immediately (fire-and-forget).
#
# Environment variables (set in .claude/settings.json or shell):
#   CLAUDE_MEMORY_SERVER_URL - default http://localhost:5098
#   CLAUDE_SESSION_ID        - session identifier

$serverUrl = $env:CLAUDE_MEMORY_SERVER_URL
if (-not $serverUrl) { $serverUrl = "http://localhost:5098" }

$sessionId = $env:CLAUDE_SESSION_ID
if (-not $sessionId) { $sessionId = "unknown" }

# Claude Code passes the Stop hook's JSON payload (session id, transcript path, etc.) as
# the first argument, same as the other hooks. Forward it as-is so the server has whatever
# session-summary material is available, instead of a fixed placeholder string.
$rawContent = $args -join " "
if (-not $rawContent) { $rawContent = "Session ended" }

$body = @{
    session_id  = $sessionId
    hook_event  = "Stop"
    raw_content = $rawContent
    metadata    = @{timestamp = (Get-Date -Format "o")} | ConvertTo-Json
} | ConvertTo-Json

try {
    Invoke-WebRequest -Uri "$serverUrl/capture/" -Method Post -Body $body -ContentType "application/json" -UseBasicParsing | Out-Null
} catch {
    # Silently ignore failures — must not block the agent
}