[CmdletBinding()]
param(
    [string]$Serial,
    [switch]$Latest,
    [string]$FileName
)

$ErrorActionPreference = 'Stop'
$packageName = 'com.melfegor.nice64'
$candidateRoot = Split-Path -Parent $PSScriptRoot
$historyRoot = (Resolve-Path (Join-Path $candidateRoot '..\..')).Path
$adb = Join-Path $historyRoot 'android-sdk\platform-tools\adb.exe'
$outputRoot = Join-Path $historyRoot 'physical-traces\touch-trigger-diagnostic1'

if (-not (Test-Path -LiteralPath $adb -PathType Leaf)) {
    throw "adb was not found at: $adb"
}

$deviceLines = & $adb devices | Select-Object -Skip 1 | Where-Object { $_ -match '\S' }
$readyDevices = @($deviceLines | ForEach-Object {
    if ($_ -match '^([^\s]+)\s+device(?:\s|$)') { $Matches[1] }
})
if ($Serial) {
    if ($readyDevices -notcontains $Serial) { throw "Requested Android device is not ready: $Serial" }
    $targetSerial = $Serial
} else {
    if ($readyDevices.Count -ne 1) {
        throw "Expected exactly one ready Android device; found $($readyDevices.Count). Use -Serial."
    }
    $targetSerial = $readyDevices[0]
}

$remoteDirectory = 'files/diagnostics'
$available = @(& $adb -s $targetSerial shell run-as $packageName ls -1 $remoteDirectory 2>&1)
if ($LASTEXITCODE -ne 0) {
    throw "run-as could not list $remoteDirectory`: $($available -join ' ')"
}
$traceNames = @($available | ForEach-Object { $_.Trim() } |
    Where-Object { $_ -match '^touch-trigger-[A-Za-z0-9-]+\.jsonl$' } |
    Sort-Object)

Write-Host 'Available controller input traces:'
$traceNames | ForEach-Object { Write-Host "  $_" }
if (-not $Latest -and -not $FileName) {
    Write-Host 'Specify -Latest or -FileName <trace.jsonl> to copy a trace.'
    return
}

$selected = if ($FileName) { $FileName } else { $traceNames | Select-Object -Last 1 }
if (-not $selected) { throw 'No controller input JSONL traces were found.' }
if ($selected -notmatch '^touch-trigger-[A-Za-z0-9-]+\.jsonl$' -or $traceNames -notcontains $selected) {
    throw "Trace filename is unavailable or invalid: $selected"
}

$sessionName = [IO.Path]::GetFileNameWithoutExtension($selected)
$localDirectory = Join-Path $outputRoot $sessionName
New-Item -ItemType Directory -Path $localDirectory -Force | Out-Null

function Copy-RunAsFile([string]$remoteName, [string]$localPath) {
    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $adb
    $startInfo.UseShellExecute = $false
    $startInfo.RedirectStandardOutput = $true
    $startInfo.RedirectStandardError = $true
    foreach ($argument in @('-s', $targetSerial, 'exec-out', 'run-as', $packageName, 'cat', "$remoteDirectory/$remoteName")) {
        [void]$startInfo.ArgumentList.Add($argument)
    }
    $process = [Diagnostics.Process]::Start($startInfo)
    $stream = [IO.File]::Open($localPath, [IO.FileMode]::Create, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $process.StandardOutput.BaseStream.CopyTo($stream) } finally { $stream.Dispose() }
    $stderr = $process.StandardError.ReadToEnd()
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) {
        Remove-Item -LiteralPath $localPath -ErrorAction SilentlyContinue
        throw "Failed to read $remoteName through run-as: $stderr"
    }
}

$localTrace = Join-Path $localDirectory $selected
Copy-RunAsFile $selected $localTrace
$traceHash = (Get-FileHash -LiteralPath $localTrace -Algorithm SHA256).Hash
Write-Host "Trace: $localTrace"
Write-Host "Trace SHA-256: $traceHash"

$summaryName = $selected -replace '\.jsonl$', '-summary.txt'
if ($available -contains $summaryName) {
    $localSummary = Join-Path $localDirectory $summaryName
    Copy-RunAsFile $summaryName $localSummary
    $summaryHash = (Get-FileHash -LiteralPath $localSummary -Algorithm SHA256).Hash
    Write-Host "Summary: $localSummary"
    Write-Host "Summary SHA-256: $summaryHash"
} else {
    Write-Host 'Matching summary: NOT FOUND'
}
