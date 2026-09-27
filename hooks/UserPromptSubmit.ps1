# Claude Code UserPromptSubmit hook — Phase 3 (Retrieval)
#
# 1. Fetches semantically relevant memories via /search/context
# 2. Injects them as context before the prompt (if any are found)
# 3. Posts the (possibly augmented) prompt to /capture/ for storage
#
# The context block is written to stdout, which Claude Code interprets as
# the new prompt — so memories are prepended before the user's actual text.
#
# Environment variables (set in .claude/settings.json or shell):
#   CLAUDE_MEMORY_SERVER_URL - default http://localhost:5098
#   CLAUDE_SESSION_ID        - session identifier

$serverUrl = $env:CLAUDE_MEMORY_SERVER_URL
if (-not $serverUrl) { $serverUrl = "http://localhost:5098" }

$sessionId = $env:CLAUDE_SESSION_ID
if (-not $sessionId) { $sessionId = "unknown" }

$prompt = $args -join " "

# ── Step 1: Fetch relevant context ──────────────────────────────
$context = $null
try {
    $contextBody = @{
        query          = $prompt
        limit          = 5
        min_similarity = 0.6
    } | ConvertTo-Json

    $response = Invoke-WebRequest -Uri "$serverUrl/search/context" -Method Post `
        -Body $contextBody -ContentType "application/json" -UseBasicParsing
    if ($response.StatusCode -eq 200) {
        $parsed = $response.Content | ConvertFrom-Json
        if ($parsed.context) {
            $context = $parsed.context
        }
    }
} catch {
    # Silently ignore — must not block the agent
}

# ── Step 2: Output augmented prompt ─────────────────────────────
if ($context) {
    # Prepend memories to the prompt so Claude sees relevant past context
    Write-Output "${context}`n`n---`n`n${prompt}"
} else {
    Write-Output $prompt
}

# ── Step 3: Fire-and-forget capture (use the ORIGINAL prompt) ───
$body = @{
    session_id  = $sessionId
    hook_event  = "UserPromptSubmit"
    raw_content = $prompt
    metadata    = @{
        timestamp      = (Get-Date -Format "o")
        context_used   = ($context -ne $null)
        context_length = if ($context) { $context.Length } else { 0 }
    } | ConvertTo-Json
} | ConvertTo-Json

try {
    Invoke-WebRequest -Uri "$serverUrl/capture/" -Method Post -Body $body `
        -ContentType "application/json" -UseBasicParsing | Out-Null
} catch {
    # Silently ignore failures — must not block the agent
}