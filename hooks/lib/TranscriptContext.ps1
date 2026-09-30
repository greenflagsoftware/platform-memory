# Shared helper for the AgentMemory hooks: builds a short "what was just said" context
# string from Claude Code's transcript (JSONL at the payload's transcript_path).
#
# The server hands this to the distiller (metadata.context) so a fragment such as
# "Yes, commit and push." can be rewritten as a standalone fact. It is bounded, never
# embedded, and never stored as a memory.
#
# Dot-source it:   . (Join-Path $PSScriptRoot 'lib\TranscriptContext.ps1')
#
# Must run under Windows PowerShell 5.1 (settings.json invokes `powershell -File`), so no
# `??`, `?.` or ternary. It must also never throw or block: any failure returns $null.

function Remove-TranscriptNoise {
    param([string]$Text)
    if (-not $Text) { return '' }
    # System reminders and our own injected memories are not conversation. Dropping the
    # latter also stops retrieved memories being fed back into new ones.
    $Text = [regex]::Replace($Text, '(?s)<system-reminder>.*?</system-reminder>', '')
    $Text = [regex]::Replace($Text, '(?s)<relevant_memories[^>]*>.*?</relevant_memories>', '')
    return $Text.Trim()
}

function Read-TranscriptTailLines {
    param([string]$Path, [int]$TailBytes)

    # The transcript is appended to while we read it, so open with shared read/write.
    $fs = [System.IO.File]::Open($Path, [System.IO.FileMode]::Open,
        [System.IO.FileAccess]::Read, [System.IO.FileShare]::ReadWrite)
    try {
        $len = $fs.Length
        $start = [Math]::Max(0, $len - $TailBytes)
        [void]$fs.Seek($start, [System.IO.SeekOrigin]::Begin)
        $buf = New-Object byte[] ([int]($len - $start))
        $read = 0
        while ($read -lt $buf.Length) {
            $n = $fs.Read($buf, $read, $buf.Length - $read)
            if ($n -le 0) { break }
            $read += $n
        }
    } finally {
        $fs.Dispose()
    }

    $text = [System.Text.Encoding]::UTF8.GetString($buf, 0, $read)
    $lines = @($text -split "`n")
    # Seeking into the middle of the file leaves a partial first line; drop it.
    if ($start -gt 0) {
        if ($lines.Count -le 1) { return @() }
        $lines = @($lines[1..($lines.Count - 1)])
    }
    return $lines
}

function Get-TranscriptContext {
    <#
    .SYNOPSIS
        Last few conversation messages from a Claude Code transcript, as bounded plain text.
    .PARAMETER TranscriptPath  transcript_path from the hook payload.
    .PARAMETER ExcludeText     The event being captured (the current prompt, or the Stop
                               hook's last assistant message). The transcript may already
                               contain it; it is removed so the context is what came BEFORE.
    .PARAMETER MaxMessages     Most recent messages to keep (consecutive same-role text
                               is merged, so ~4 is about two turns).
    .PARAMETER MaxChars        Hard cap on the returned string (the server also caps at 2000).
    .PARAMETER TailBytes       How much of the end of the file to read.
    #>
    param(
        [string]$TranscriptPath,
        [string]$ExcludeText,
        [int]$MaxMessages = 4,
        [int]$MaxChars = 2000,
        [int]$TailBytes = 262144
    )

    try {
        if (-not $TranscriptPath -or -not (Test-Path -LiteralPath $TranscriptPath -PathType Leaf)) {
            return $null
        }

        $messages = New-Object System.Collections.ArrayList
        foreach ($line in (Read-TranscriptTailLines -Path $TranscriptPath -TailBytes $TailBytes)) {
            if (-not $line -or $line.Length -lt 2) { continue }

            $entry = $null
            try { $entry = $line | ConvertFrom-Json } catch { continue }
            if (-not $entry) { continue }
            if ($entry.type -ne 'user' -and $entry.type -ne 'assistant') { continue }
            # Sub-agent chatter and harness-injected messages (skill bodies etc.) aren't dialogue.
            if ($entry.isSidechain -eq $true -or $entry.isMeta -eq $true) { continue }

            $content = $entry.message.content
            $text = ''
            if ($content -is [string]) {
                $text = $content
            } elseif ($content -is [System.Array]) {
                # Only text blocks: tool_use, tool_result and thinking are not conversation.
                $parts = @($content | Where-Object { $_.type -eq 'text' } | ForEach-Object { $_.text })
                $text = $parts -join "`n"
            }

            $text = Remove-TranscriptNoise -Text $text
            if (-not $text) { continue }

            $role = $entry.type
            $last = if ($messages.Count -gt 0) { $messages[$messages.Count - 1] } else { $null }
            if ($last -and $last.Role -eq $role) {
                # e.g. assistant text before and after a run of tool calls
                $last.Text = $last.Text + "`n" + $text
            } else {
                [void]$messages.Add([pscustomobject]@{ Role = $role; Text = $text })
            }
        }

        # Remove the event being captured if the transcript already holds it.
        $exclude = if ($ExcludeText) { $ExcludeText.Trim() } else { '' }
        if ($exclude -and $messages.Count -gt 0) {
            $last = $messages[$messages.Count - 1]
            $idx = $last.Text.LastIndexOf($exclude, [System.StringComparison]::Ordinal)
            if ($idx -ge 0) {
                $remainder = ($last.Text.Remove($idx, $exclude.Length)).Trim()
                if ($remainder) { $last.Text = $remainder } else { $messages.RemoveAt($messages.Count - 1) }
            }
        }

        if ($messages.Count -eq 0) { return $null }

        $keep = @($messages | Select-Object -Last $MaxMessages)
        $perMessage = [Math]::Max(80, [int]($MaxChars / $keep.Count))

        $rendered = foreach ($m in $keep) {
            $t = $m.Text
            if ($t.Length -gt $perMessage) {
                if ($m.Role -eq 'assistant') {
                    # An assistant message ends with the question or proposal the user answers.
                    $t = '...' + $t.Substring($t.Length - $perMessage)
                } else {
                    $t = $t.Substring(0, $perMessage) + '...'
                }
            }
            $label = if ($m.Role -eq 'assistant') { 'Assistant' } else { 'User' }
            "${label}: $t"
        }

        $result = $rendered -join "`n"
        if ($result.Length -gt $MaxChars) {
            $result = $result.Substring($result.Length - $MaxChars)   # keep the most recent
        }
        return $result
    } catch {
        return $null
    }
}
