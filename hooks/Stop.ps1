# Claude Code Stop hook
# Fires when a Claude Code session ends. Posts session metadata to the local
# AgentMemory server for capture and exits immediately (fire-and-forget).
#
# Claude Code invokes this hook with the event payload as JSON on STDIN, not
# as a command-line argument. The payload includes session_id and, when
# available, the last assistant message.
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

$rawContent = $null
if ($payload -and ($payload.PSObject.Properties.Name -contains "last_assistant_message")) {
    $rawContent = $payload.last_assistant_message
}
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

exit 0
