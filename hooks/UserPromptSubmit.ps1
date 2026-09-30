# Claude Code UserPromptSubmit hook — Phase 3 (Retrieval)
#
# 1. Fetches semantically relevant memories via /search/context
# 2. Injects them as context before the prompt, via the hookSpecificOutput/
#    additionalContext stdout contract (NOT plain stdout text — Claude Code
#    only treats the prompt itself as raw text; context injection requires
#    the structured JSON envelope below)
# 3. Posts the (possibly augmented) prompt to /capture/ for storage
#
# Claude Code invokes this hook with the event payload as JSON on STDIN, not
# as a command-line argument — there is no "$input" shell variable. The
# payload includes session_id and a prompt field (seen as either "prompt" or
# "prompt_text" depending on version), among other fields.
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

$prompt = $null
if ($payload) {
    $propNames = $payload.PSObject.Properties.Name
    if ($propNames -contains "prompt_text") { $prompt = $payload.prompt_text }
    elseif ($propNames -contains "prompt") { $prompt = $payload.prompt }
}
if (-not $prompt) { $prompt = "" }

# ── Step 1: Fetch relevant context ──────────────────────────────
$context = $null
try {
    $contextBody = @{
        query          = $prompt
        limit          = 5
        min_similarity = 0.75
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

# ── Step 2: Emit structured context injection ───────────────────
# Claude Code only injects additional context when stdout is this JSON
# envelope (exit code 0); arbitrary stdout text is not prepended to the
# prompt the way earlier versions of this hook assumed.
if ($context) {
    $output = @{
        hookSpecificOutput = @{
            hookEventName     = "UserPromptSubmit"
            additionalContext = $context
        }
    } | ConvertTo-Json -Depth 5 -Compress
    Write-Output $output
}

# ── Conversation context for the distiller (Phase 4) ──────────
# Loaded defensively: if the helper is missing or broken the hook still captures.
$contextText = $null
try {
    . (Join-Path $PSScriptRoot 'lib\TranscriptContext.ps1')
    $transcriptPath = $null
    if ($payload -and ($payload.PSObject.Properties.Name -contains "transcript_path")) {
        $transcriptPath = $payload.transcript_path
    }
    $contextText = Get-TranscriptContext -TranscriptPath $transcriptPath -ExcludeText $prompt
} catch {}

# ── Step 3: Fire-and-forget capture (use the ORIGINAL prompt) ───
$body = @{
    session_id  = $sessionId
    hook_event  = "UserPromptSubmit"
    raw_content = $prompt
    metadata    = @{
        timestamp      = (Get-Date -Format "o")
        context_used   = ($context -ne $null)
        context_length = if ($context) { $context.Length } else { 0 }
        # Previous conversation turns for the distiller (not the injected memories above).
        context        = $contextText
    } | ConvertTo-Json
} | ConvertTo-Json

try {
    Invoke-WebRequest -Uri "$serverUrl/capture/" -Method Post -Body $body `
        -ContentType "application/json" -UseBasicParsing | Out-Null
} catch {
    # Silently ignore failures — must not block the agent
}

exit 0
