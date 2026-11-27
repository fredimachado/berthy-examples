# Shared Kestrel/process stdout+stderr drain helpers for PublishedWeb verify.
# Drains always run until EOF; listen-URL discovery is independent and must not stop them.

function New-StreamDrainState {
    param(
        [Parameter(Mandatory = $false)]
        [int] $MaxChars = 65536
    )

    return [hashtable]::Synchronized(@{
            Url       = $null
            Captured  = [System.Text.StringBuilder]::new()
            MaxChars  = $MaxChars
            Gate      = [object]::new()
            LinesRead = 0
        })
}

function Get-StreamDrainCapture {
    param(
        [Parameter(Mandatory = $true)]
        [hashtable] $State
    )

    $gate = $State['Gate']
    [System.Threading.Monitor]::Enter($gate)
    try {
        return [string]$State['Captured'].ToString()
    }
    finally {
        [System.Threading.Monitor]::Exit($gate)
    }
}

function Start-StreamDrain {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)]
        [hashtable] $State
    )

    $drainScript = {
        param(
            [System.IO.StreamReader] $Reader,
            [hashtable] $State
        )
        try {
            while ($null -ne ($line = $Reader.ReadLine())) {
                if ($null -eq $State['Url'] -and $line -match 'Now listening on:\s*(https?://\S+)') {
                    $State['Url'] = $Matches[1].TrimEnd('/')
                }

                $gate = $State['Gate']
                [System.Threading.Monitor]::Enter($gate)
                try {
                    $State['LinesRead'] = [int]$State['LinesRead'] + 1

                    $captured = $State['Captured']
                    $maxChars = [int]$State['MaxChars']
                    if ($captured.Length -ge $maxChars) {
                        continue
                    }

                    $chunk = $line + [Environment]::NewLine
                    $room = $maxChars - $captured.Length
                    if ($chunk.Length -gt $room) {
                        $chunk = $chunk.Substring(0, $room)
                    }
                    [void]$captured.Append($chunk)
                }
                finally {
                    [System.Threading.Monitor]::Exit($gate)
                }
            }
        }
        catch {
            # Stream closes when the process exits; drain runspace ends.
        }
    }

    $jobs = @()
    foreach ($reader in @($Process.StandardOutput, $Process.StandardError)) {
        $ps = [powershell]::Create()
        [void]$ps.AddScript($drainScript).AddArgument($reader).AddArgument($State)
        $handle = $ps.BeginInvoke()
        $jobs += [pscustomobject]@{
            PowerShell = $ps
            Handle     = $handle
        }
    }

    return , $jobs
}

function Wait-StreamDrainJobs {
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $DrainJobs,
        [Parameter(Mandatory = $true)]
        [datetime] $Deadline
    )

    foreach ($job in $DrainJobs) {
        $remainingMs = [int][Math]::Ceiling(($Deadline - [DateTime]::UtcNow).TotalMilliseconds)
        if ($remainingMs -le 0) {
            return $false
        }

        if (-not $job.Handle.IsCompleted) {
            if (-not $job.Handle.AsyncWaitHandle.WaitOne($remainingMs)) {
                return $false
            }
        }

        try {
            $job.PowerShell.EndInvoke($job.Handle) | Out-Null
        }
        catch {
            # EndInvoke after Stop/dispose is best-effort.
        }
    }

    return $true
}

function Wait-StreamDrainListenUrl {
    param(
        [Parameter(Mandatory = $true)]
        [System.Diagnostics.Process] $Process,
        [Parameter(Mandatory = $true)]
        [hashtable] $State,
        [Parameter(Mandatory = $true)]
        [object[]] $DrainJobs,
        [Parameter(Mandatory = $true)]
        [datetime] $Deadline
    )

    while ([DateTime]::UtcNow -lt $Deadline) {
        if ($State['Url']) {
            return [string]$State['Url']
        }

        if ($Process.HasExited) {
            if (-not (Wait-StreamDrainJobs -DrainJobs $DrainJobs -Deadline $Deadline)) {
                throw "Timed out waiting for stream drains after process exit (code $($Process.ExitCode)). captured=$(Get-StreamDrainCapture -State $State)"
            }

            if ($State['Url']) {
                return [string]$State['Url']
            }

            throw "Published API exited before listening (code $($Process.ExitCode)). captured=$(Get-StreamDrainCapture -State $State)"
        }

        Start-Sleep -Milliseconds 50
    }

    throw "Timed out waiting for published Kestrel listen URL. captured=$(Get-StreamDrainCapture -State $State)"
}

function Stop-StreamDrain {
    param(
        [Parameter(Mandatory = $false)]
        [object[]] $DrainJobs
    )

    if ($null -eq $DrainJobs) {
        return
    }

    foreach ($job in $DrainJobs) {
        try {
            if (-not $job.Handle.IsCompleted) {
                $job.PowerShell.Stop()
            }
        }
        catch {
        }

        try {
            if ($job.Handle.IsCompleted) {
                $job.PowerShell.EndInvoke($job.Handle) | Out-Null
            }
        }
        catch {
        }

        try {
            $job.PowerShell.Dispose()
        }
        catch {
        }
    }
}
