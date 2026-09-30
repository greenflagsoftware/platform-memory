# Tests for hooks/lib/TranscriptContext.ps1. Plain assertions (no Pester needed) so they run
# under the same Windows PowerShell 5.1 the hooks use, and under PowerShell 7:
#
#   powershell -NoProfile -File hooks/tests/TranscriptContext.Tests.ps1
#   pwsh       -NoProfile -File hooks/tests/TranscriptContext.Tests.ps1
#
# Exits non-zero if any assertion fails.

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '..\lib\TranscriptContext.ps1')

$script:failures = 0
$script:total = 0
function Assert-True($cond, $name) {
    $script:total++
    if ($cond) { Write-Host "  PASS  $name" }
    else { Write-Host "  FAIL  $name" -ForegroundColor Red; $script:failures++ }
}

$tmp = Join-Path ([System.IO.Path]::GetTempPath()) ("ctx-tests-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $tmp | Out-Null

# Build transcript lines in the shape Claude Code writes them.
function New-User($text) { (@{ type = 'user'; isSidechain = $false; message = @{ role = 'user'; content = $text } } | ConvertTo-Json -Depth 6 -Compress) }
function New-Assistant($text) { (@{ type = 'assistant'; isSidechain = $false; message = @{ role = 'assistant'; content = @(@{ type = 'text'; text = $text }) } } | ConvertTo-Json -Depth 6 -Compress) }
function New-ToolUse { (@{ type = 'assistant'; isSidechain = $false; message = @{ role = 'assistant'; content = @(@{ type = 'tool_use'; id = 't1'; name = 'Bash'; input = @{ command = 'ls' } }) } } | ConvertTo-Json -Depth 8 -Compress) }
function New-ToolResult($text) { (@{ type = 'user'; isSidechain = $false; message = @{ role = 'user'; content = @(@{ type = 'tool_result'; tool_use_id = 't1'; content = $text }) } } | ConvertTo-Json -Depth 8 -Compress) }
function New-Thinking { (@{ type = 'assistant'; isSidechain = $false; message = @{ role = 'assistant'; content = @(@{ type = 'thinking'; thinking = 'secret reasoning' }) } } | ConvertTo-Json -Depth 6 -Compress) }
function Write-Transcript($name, $lines) {
    $path = Join-Path $tmp $name
    [System.IO.File]::WriteAllText($path, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))
    return $path
}

try {
    Write-Host 'basic dialogue'
    $p = Write-Transcript 'basic.jsonl' @(
        (New-User 'Should I commit the hook fixes?'),
        (New-Assistant 'The hook fixes are ready. Want me to commit and push them?')
    )
    $ctx = Get-TranscriptContext -TranscriptPath $p -ExcludeText 'Yes, commit and push.'
    Assert-True ($ctx -like '*User: Should I commit the hook fixes?*') 'includes earlier user message'
    Assert-True ($ctx -like '*Assistant: The hook fixes are ready. Want me to commit and push them?*') 'includes assistant question'
    Assert-True ($ctx.IndexOf('User:') -lt $ctx.IndexOf('Assistant:')) 'keeps chronological order'

    Write-Host 'non-dialogue entries are skipped'
    $sidechain = (@{ type = 'assistant'; isSidechain = $true; message = @{ role = 'assistant'; content = @(@{ type = 'text'; text = 'SIDECHAIN-NOISE' }) } } | ConvertTo-Json -Depth 6 -Compress)
    $meta = (@{ type = 'user'; isMeta = $true; message = @{ role = 'user'; content = 'META-NOISE skill body' } } | ConvertTo-Json -Depth 6 -Compress)
    $p = Write-Transcript 'noise.jsonl' @(
        (New-User 'Run the tests'), (New-Thinking), (New-ToolUse), (New-ToolResult 'TOOL-RESULT-NOISE'),
        $sidechain, $meta, '{"type":"attachment","x":1}', 'not json at all',
        (New-Assistant 'All 59 tests pass.')
    )
    $ctx = Get-TranscriptContext -TranscriptPath $p
    Assert-True ($ctx -like '*Run the tests*' -and $ctx -like '*All 59 tests pass.*') 'keeps real messages'
    Assert-True ($ctx -notlike '*TOOL-RESULT-NOISE*') 'drops tool results'
    Assert-True ($ctx -notlike '*secret reasoning*') 'drops thinking'
    Assert-True ($ctx -notlike '*SIDECHAIN-NOISE*') 'drops sidechain messages'
    Assert-True ($ctx -notlike '*META-NOISE*') 'drops meta messages'

    Write-Host 'system reminders and injected memories are stripped'
    $p = Write-Transcript 'strip.jsonl' @(
        (New-User "<system-reminder>REMINDER-NOISE</system-reminder>Fix the gate`n<relevant_memories from=`"agent-memory`">MEMORY-NOISE</relevant_memories>")
    )
    $ctx = Get-TranscriptContext -TranscriptPath $p
    Assert-True ($ctx -like '*Fix the gate*') 'keeps the prompt text'
    Assert-True ($ctx -notlike '*REMINDER-NOISE*' -and $ctx -notlike '*MEMORY-NOISE*') 'removes reminder and memory blocks'

    Write-Host 'the event being captured is excluded'
    $p = Write-Transcript 'exclude.jsonl' @(
        (New-User 'Earlier question'), (New-Assistant 'Earlier answer'),
        (New-User 'Yes, commit and push.')
    )
    $ctx = Get-TranscriptContext -TranscriptPath $p -ExcludeText 'Yes, commit and push.'
    Assert-True ($ctx -notlike '*commit and push*') 'current prompt removed when already in transcript'
    Assert-True ($ctx -like '*Earlier answer*') 'earlier turns remain'
    $p = Write-Transcript 'exclude-stop.jsonl' @(
        (New-User 'Do the thing'), (New-Assistant 'Working on it.'), (New-ToolUse), (New-ToolResult 'x'), (New-Assistant 'Done: FINAL-MESSAGE')
    )
    $ctx = Get-TranscriptContext -TranscriptPath $p -ExcludeText 'Done: FINAL-MESSAGE'
    Assert-True ($ctx -notlike '*FINAL-MESSAGE*') 'stop hook: last assistant message removed from context'
    Assert-True ($ctx -like '*Working on it.*') 'stop hook: earlier assistant text in the same turn is kept'
    $ctx = Get-TranscriptContext -TranscriptPath $p -ExcludeText 'text that is not there'
    Assert-True ($ctx -like '*FINAL-MESSAGE*') 'nothing removed when the excluded text is absent'

    Write-Host 'size bounds'
    $long = ('x' * 5000)
    $p = Write-Transcript 'long.jsonl' @(
        (New-User ('START-' + $long)),
        (New-Assistant ($long + '-END-QUESTION?'))
    )
    $ctx = Get-TranscriptContext -TranscriptPath $p -MaxChars 600
    Assert-True ($ctx.Length -le 600) "result never exceeds MaxChars ($($ctx.Length) <= 600)"
    Assert-True ($ctx -like '*END-QUESTION?') 'long assistant message keeps its tail'
    $many = 1..30 | ForEach-Object { New-User "message number $_"; New-Assistant "reply number $_" }
    $p = Write-Transcript 'many.jsonl' $many
    $ctx = Get-TranscriptContext -TranscriptPath $p -MaxMessages 2
    Assert-True ($ctx -like '*reply number 30*' -and $ctx -notlike '*reply number 29*') 'keeps only the most recent MaxMessages'

    Write-Host 'large files: only the tail is read'
    $filler = 1..400 | ForEach-Object { New-ToolResult ('y' * 2000) }
    $p = Write-Transcript 'big.jsonl' (@((New-User 'ANCIENT-MESSAGE')) + $filler + @((New-User 'Recent question'), (New-Assistant 'Recent answer')))
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $ctx = Get-TranscriptContext -TranscriptPath $p -TailBytes 65536
    $sw.Stop()
    Assert-True ($ctx -like '*Recent answer*' -and $ctx -notlike '*ANCIENT-MESSAGE*') 'ignores the head of a large file'
    Assert-True ($sw.ElapsedMilliseconds -lt 2000) "fast on a large file ($($sw.ElapsedMilliseconds) ms)"

    Write-Host 'never throws, returns null on bad input'
    Assert-True ($null -eq (Get-TranscriptContext -TranscriptPath (Join-Path $tmp 'missing.jsonl'))) 'missing file -> null'
    Assert-True ($null -eq (Get-TranscriptContext -TranscriptPath $null)) 'no path -> null'
    Assert-True ($null -eq (Get-TranscriptContext -TranscriptPath (Write-Transcript 'empty.jsonl' @()))) 'empty file -> null'
    Assert-True ($null -eq (Get-TranscriptContext -TranscriptPath (Write-Transcript 'garbage.jsonl' @('{{{', 'nope')))) 'unparseable file -> null'

    Write-Host 'non-ASCII text survives'
    # Built from code points: PowerShell 5.1 reads BOM-less script files as ANSI, so a literal
    # non-ASCII string here would be mangled identically on both sides and prove nothing.
    $uni = 'caf' + [char]0xE9 + ' ' + [char]0x2014 + ' ' + [char]0x65E5 + [char]0x672C
    $p = Write-Transcript 'utf8.jsonl' @((New-User "Use the $uni approach"))
    Assert-True ((Get-TranscriptContext -TranscriptPath $p).Contains($uni)) 'unicode preserved'
}
finally {
    Remove-Item -LiteralPath $tmp -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host ("{0} assertions, {1} failed" -f $script:total, $script:failures)
if ($script:failures -gt 0) { exit 1 }
exit 0
