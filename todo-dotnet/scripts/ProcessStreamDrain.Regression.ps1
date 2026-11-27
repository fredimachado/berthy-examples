#Requires -Version 7
# Regression for drain-for-lifetime: after the listen URL is seen, drains must keep
# reading or a chatty child blocks on a full pipe and never exits.
# The helper IS the invariant — Wait-StreamDrainListenUrl never stops Start-StreamDrain.
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'ProcessStreamDrain.ps1')

$childCommand = @'
$ErrorActionPreference = 'Stop'
[Console]::Out.WriteLine('Now listening on: http://127.0.0.1:9')
[Console]::Out.Flush()
$block = 'x' * 16384
for ($i = 0; $i -lt 32; $i++) {
    [Console]::Out.WriteLine("OUT-$i-$block")
    [Console]::Error.WriteLine("ERR-$i-$block")
}
[Console]::Out.WriteLine('DRAIN_CHILD_DONE')
[Console]::Out.Flush()
'@

$info = [System.Diagnostics.ProcessStartInfo]::new()
$info.FileName = 'pwsh'
$info.ArgumentList.Add('-NoProfile')
$info.ArgumentList.Add('-Command')
$info.ArgumentList.Add($childCommand)
$info.UseShellExecute = $false
$info.RedirectStandardOutput = $true
$info.RedirectStandardError = $true
$info.CreateNoWindow = $true

$process = [System.Diagnostics.Process]::new()
$process.StartInfo = $info
if (-not $process.Start()) {
    throw 'Failed to start drain regression child.'
}

$state = New-StreamDrainState
$drainJobs = Start-StreamDrain -Process $process -State $state
try {
    $deadline = [DateTime]::UtcNow.AddSeconds(30)
    $url = Wait-StreamDrainListenUrl `
        -Process $process `
        -State $state `
        -DrainJobs $drainJobs `
        -Deadline $deadline
    if ($url -ne 'http://127.0.0.1:9') {
        throw "Unexpected listen URL '$url'."
    }

    # If drains had stopped at the URL, the child would block here on a full pipe.
    if (-not $process.WaitForExit(30000)) {
        try { $process.Kill($true) } catch { }
        throw 'Child hung after listen URL — drains likely stopped at readiness (pipe full).'
    }

    if ($process.ExitCode -ne 0) {
        throw "Child exited with code $($process.ExitCode)."
    }

    $drainDeadline = [DateTime]::UtcNow.AddSeconds(10)
    if (-not (Wait-StreamDrainJobs -DrainJobs $drainJobs -Deadline $drainDeadline)) {
        throw 'Timed out waiting for drain jobs after child exit.'
    }

    # Capture is bounded; LinesRead counts every drained line including past the cap.
    $linesRead = [int]$state['LinesRead']
    if ($linesRead -lt 60) {
        throw "Expected drains to read the post-URL flood (linesRead=$linesRead)."
    }

    $capture = Get-StreamDrainCapture -State $state
    if ($capture -notmatch 'Now listening on:\s*http://127\.0\.0\.1:9') {
        throw "Capture missing listen URL. capture=$($capture.Substring(0, [Math]::Min(200, $capture.Length)))"
    }
}
finally {
    Stop-StreamDrain -DrainJobs $drainJobs
    if (-not $process.HasExited) {
        try { $process.Kill($true) } catch { }
    }
    $process.Dispose()
}

Write-Host 'ProcessStreamDrain regression passed'
