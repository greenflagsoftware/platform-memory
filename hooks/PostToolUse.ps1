# Claude Code PostToolUse hook
# Fires after the agent uses a tool. Posts the tool-call details to the local
# AgentMemory server for capture and exits immediately (fire-and-forget).
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

$rawContent = "${toolName}: ${toolInputJson}"
if ($toolResult) { $rawContent = "$rawContent -> $toolResult" }

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
