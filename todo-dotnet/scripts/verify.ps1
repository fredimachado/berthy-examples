#Requires -Version 7
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Api', 'AspireWeb', 'PublishedWeb')]
    [string] $Target,

    [Parameter(Mandatory = $false)]
    [string] $PublishDir
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$todoAppHost = (Resolve-Path (Join-Path $repoRoot 'todo-dotnet\AppHost\apphost.cs')).Path
$smokeScript = Join-Path $PSScriptRoot 'smoke.ps1'
$webDir = Join-Path $repoRoot 'todo-dotnet\web'

function Normalize-AppHostPath([string] $path) {
    return [System.IO.Path]::GetFullPath($path).TrimEnd('\', '/').ToLowerInvariant()
}

function Assert-NativeExit([string] $commandName) {
    if ($LASTEXITCODE -ne 0) {
        throw "$commandName exited with code $LASTEXITCODE."
    }
}

function Get-FreeTcpPort {
    $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
    $listener.Start()
    try {
        return [int]$listener.LocalEndpoint.Port
    }
    finally {
        $listener.Stop()
    }
}

function Wait-HttpOk {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Url,
        [Parameter(Mandatory = $true)]
        [TimeSpan] $Timeout
    )

    $deadline = [DateTime]::UtcNow + $Timeout
    $lastError = $null
    while ([DateTime]::UtcNow -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -Method GET -TimeoutSec 5
            if ([int]$response.StatusCode -eq 200) {
                return
            }
            $lastError = "HTTP $($response.StatusCode)"
        }
        catch {
            $lastError = $_.Exception.Message
        }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for $Url to return 200. Last error: $lastError"
}

function Get-ResourceHttpUrl {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Apphost,
        [Parameter(Mandatory = $true)]
        [string] $DisplayName
    )

    $describeJson = aspire describe $DisplayName --format Json --apphost $Apphost --non-interactive --nologo | Out-String
    Assert-NativeExit 'aspire describe'
    $snapshot = $describeJson | ConvertFrom-Json
    $resources = @()
    if ($snapshot.resources) {
        $resources = @($snapshot.resources)
    }
    elseif ($snapshot.name -or $snapshot.displayName) {
        $resources = @($snapshot)
    }

    # Isolated starts suffix the resource name (api-xxxxxx); displayName stays stable.
    $resource = $resources | Where-Object {
        [string]$_.displayName -eq $DisplayName -or
        [string]$_.name -eq $DisplayName -or
        [string]$_.name -like "$DisplayName-*"
    } | Select-Object -First 1

    if (-not $resource) {
        throw "aspire describe $DisplayName returned no matching resource: $describeJson"
    }

    $urls = @($resource.urls)
    $http = $urls | Where-Object { [string]$_.name -eq 'http' } | Select-Object -First 1
    if (-not $http) {
        $http = $urls | Where-Object { [string]$_.url -like 'http://*' } | Select-Object -First 1
    }

    if (-not $http) {
        throw "$DisplayName resource has no http endpoint: $describeJson"
    }

    $url = [string]$http.url
    if ([string]::IsNullOrWhiteSpace($url)) {
        throw "$DisplayName http endpoint URL is empty: $describeJson"
    }

    return $url.TrimEnd('/')
}

function Start-OwnedProcess {
    param(
        [Parameter(Mandatory = $true)]
        [string] $FileName,
        [Parameter(Mandatory = $true)]
        [string[]] $Arguments,
        [Parameter(Mandatory = $true)]
        [string] $WorkingDirectory,
        [Parameter(Mandatory = $true)]
        [hashtable] $Environment,
        [Parameter(Mandatory = $false)]
        [switch] $Wait
    )

    $info = [System.Diagnostics.ProcessStartInfo]::new()
    $info.FileName = $FileName
    foreach ($arg in $Arguments) {
        $info.ArgumentList.Add($arg)
    }
    $info.WorkingDirectory = $WorkingDirectory
    $info.UseShellExecute = $false
    $info.RedirectStandardOutput = $true
    $info.RedirectStandardError = $true
    $info.CreateNoWindow = $true
    foreach ($key in $Environment.Keys) {
        $info.Environment[$key] = [string]$Environment[$key]
    }

    $process = [System.Diagnostics.Process]::new()
    $process.StartInfo = $info
    $process.EnableRaisingEvents = $true
    if (-not $process.Start()) {
        throw "Failed to start $FileName."
    }

    if ($Wait) {
        $stdoutTask = $process.StandardOutput.ReadToEndAsync()
        $stderrTask = $process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        return [pscustomobject]@{
            Process = $process
            ExitCode = $process.ExitCode
            StdOut = $stdoutTask.GetAwaiter().GetResult()
            StdErr = $stderrTask.GetAwaiter().GetResult()
        }
    }

    return [pscustomobject]@{
        Process = $process
        ExitCode = $null
        StdOut = $null
        StdErr = $null
    }
}

function Invoke-AspireTargets {
    $todoAppHostPid = $null
    $runError = $null
    $cleanupError = $null

    try {
        Write-Host "Starting AppHost: $todoAppHost"
        $startJson = aspire start --apphost $todoAppHost --isolated --non-interactive --format Json --nologo | Out-String
        Assert-NativeExit 'aspire start'

        $started = $startJson | ConvertFrom-Json
        if (-not $started.appHostPath) {
            throw "aspire start JSON missing appHostPath: $startJson"
        }
        if ((Normalize-AppHostPath ([string]$started.appHostPath)) -ne (Normalize-AppHostPath $todoAppHost)) {
            throw "aspire start path '$($started.appHostPath)' does not match owned AppHost '$todoAppHost'."
        }

        $rawPid = $started.appHostPid
        if ($null -eq $rawPid -or [string]::IsNullOrWhiteSpace([string]$rawPid)) {
            throw "aspire start JSON missing appHostPid: $startJson"
        }
        $parsedPid = 0
        if (-not [int]::TryParse([string]$rawPid, [ref]$parsedPid) -or $parsedPid -le 0) {
            throw "aspire start returned non-positive appHostPid: $rawPid"
        }
        $todoAppHostPid = $parsedPid
        Write-Host "Owned AppHost PID: $todoAppHostPid"

        aspire wait api --status healthy --apphost $todoAppHost --non-interactive --nologo | Out-Host
        Assert-NativeExit 'aspire wait api'

        if ($Target -eq 'Api') {
            $apiUrl = Get-ResourceHttpUrl -Apphost $todoAppHost -DisplayName 'api'
            Write-Host "API http endpoint: $apiUrl"

            & $smokeScript -ApiUrl $apiUrl
            if ($LASTEXITCODE -ne 0) {
                throw "smoke.ps1 failed with exit code $LASTEXITCODE"
            }
        }
        elseif ($Target -eq 'AspireWeb') {
            aspire wait web --status healthy --apphost $todoAppHost --non-interactive --nologo | Out-Host
            Assert-NativeExit 'aspire wait web'

            $webUrl = Get-ResourceHttpUrl -Apphost $todoAppHost -DisplayName 'web'
            Write-Host "Web http endpoint: $webUrl"

            # Scope TODO_BASE_URL to this try; restore the caller's value in finally.
            # Do not Push-Location: npm uses --prefix, and a failed cd would leave the temp URL set.
            $hadTodoBaseUrl = Test-Path Env:TODO_BASE_URL
            $previousTodoBaseUrl = if ($hadTodoBaseUrl) { $env:TODO_BASE_URL } else { $null }
            try {
                $env:TODO_BASE_URL = $webUrl
                npm --prefix $webDir run test:e2e
                if ($LASTEXITCODE -ne 0) {
                    throw "npm run test:e2e failed with exit code $LASTEXITCODE"
                }
            }
            finally {
                if ($hadTodoBaseUrl) {
                    $env:TODO_BASE_URL = $previousTodoBaseUrl
                }
                else {
                    Remove-Item Env:TODO_BASE_URL -ErrorAction SilentlyContinue
                }
            }
        }
    }
    catch {
        $runError = $_
    }
    finally {
        if ($null -ne $todoAppHostPid) {
            try {
                Write-Host "Stopping AppHost PID $todoAppHostPid"
                aspire stop --pid $todoAppHostPid --apphost $todoAppHost --non-interactive --nologo | Out-Host
                Assert-NativeExit 'aspire stop'
            }
            catch {
                $cleanupError = $_
            }
        }
    }

    if ($null -ne $cleanupError -and $null -ne $runError) {
        throw "verify failed with run error and AppHost cleanup failure.`nRun error: $runError`nCleanup error: $cleanupError"
    }

    if ($null -ne $cleanupError) {
        throw "AppHost cleanup failed: $cleanupError"
    }

    if ($null -ne $runError) {
        throw $runError
    }
}

function Invoke-PublishedWeb {
    if ([string]::IsNullOrWhiteSpace($PublishDir)) {
        throw "-PublishDir is required for -Target PublishedWeb."
    }

    $resolvedPublish = (Resolve-Path $PublishDir).Path
    $dll = Join-Path $resolvedPublish 'Todo.Api.dll'
    if (-not (Test-Path -LiteralPath $dll)) {
        throw "Published Todo.Api.dll not found at $dll"
    }

    $indexHtml = Join-Path $resolvedPublish 'wwwroot\index.html'
    if (-not (Test-Path -LiteralPath $indexHtml)) {
        throw "Published wwwroot/index.html missing at $indexHtml (frontend was not included in publish)."
    }

    $pgName = 'todo-dotnet-pg-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $redisName = 'todo-dotnet-redis-' + [guid]::NewGuid().ToString('N').Substring(0, 12)
    $pgPort = Get-FreeTcpPort
    $redisPort = Get-FreeTcpPort
    $apiProcess = $null
    $runError = $null
    $cleanupError = $null
    $replica = 'verify-published'

    try {
        Write-Host "Starting owned Postgres ($pgName) on 127.0.0.1:$pgPort"
        docker run -d --rm --name $pgName `
            -e POSTGRES_USER=todo `
            -e POSTGRES_PASSWORD=todo-verify `
            -e POSTGRES_DB=tododb `
            -p "127.0.0.1:${pgPort}:5432" `
            postgres:17 | Out-Host
        Assert-NativeExit 'docker run postgres'

        Write-Host "Starting owned Redis ($redisName) on 127.0.0.1:$redisPort"
        docker run -d --rm --name $redisName `
            -p "127.0.0.1:${redisPort}:6379" `
            redis:7 | Out-Host
        Assert-NativeExit 'docker run redis'

        $readyDeadline = [DateTime]::UtcNow + [TimeSpan]::FromMinutes(2)
        do {
            docker exec $pgName pg_isready -U todo -d tododb | Out-Null
            if ($LASTEXITCODE -eq 0) { break }
            if ([DateTime]::UtcNow -ge $readyDeadline) {
                throw "Postgres container $pgName did not become ready."
            }
            Start-Sleep -Milliseconds 500
        } while ($true)

        do {
            docker exec $redisName redis-cli ping | Out-Null
            if ($LASTEXITCODE -eq 0) { break }
            if ([DateTime]::UtcNow -ge $readyDeadline) {
                throw "Redis container $redisName did not become ready."
            }
            Start-Sleep -Milliseconds 500
        } while ($true)

        $tododb = "Host=127.0.0.1;Port=$pgPort;Database=tododb;Username=todo;Password=todo-verify"
        $redis = "127.0.0.1:$redisPort"

        Write-Host "Running migrate from $resolvedPublish"
        $migrate = Start-OwnedProcess -FileName 'dotnet' -Arguments @($dll, 'migrate') `
            -WorkingDirectory $resolvedPublish `
            -Environment @{
                'ConnectionStrings__tododb' = $tododb
                'ASPNETCORE_URLS' = ''
            } -Wait
        if ($migrate.ExitCode -ne 0) {
            throw "dotnet Todo.Api.dll migrate exited with code $($migrate.ExitCode). stderr=$($migrate.StdErr) stdout=$($migrate.StdOut)"
        }
        $migrate.Process.Dispose()

        Write-Host "Starting published API on http://127.0.0.1:0"
        $listenState = [hashtable]::Synchronized(@{
                Url = $null
                Log = [System.Text.StringBuilder]::new()
            })

        $info = [System.Diagnostics.ProcessStartInfo]::new()
        $info.FileName = 'dotnet'
        $info.ArgumentList.Add($dll)
        $info.WorkingDirectory = $resolvedPublish
        $info.UseShellExecute = $false
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $info.CreateNoWindow = $true
        $info.Environment['ConnectionStrings__tododb'] = $tododb
        $info.Environment['ConnectionStrings__redis'] = $redis
        $info.Environment['ASPNETCORE_URLS'] = 'http://127.0.0.1:0'
        $info.Environment['HOSTNAME'] = $replica
        $info.Environment['BuildVersion'] = 'verify-published'
        $info.Environment['BuildCommit'] = 'verify'

        $apiProcess = [System.Diagnostics.Process]::new()
        $apiProcess.StartInfo = $info
        $apiProcess.EnableRaisingEvents = $true
        $onLine = {
            param($sender, $e)
            if ($null -eq $e.Data) { return }
            [void]$listenState.Log.AppendLine($e.Data)
            Write-Host $e.Data
            if ($e.Data -match 'Now listening on:\s*(https?://\S+)') {
                $listenState.Url = $Matches[1].TrimEnd('/')
            }
        }.GetNewClosure()
        $apiProcess.add_OutputDataReceived($onLine)
        $apiProcess.add_ErrorDataReceived($onLine)
        if (-not $apiProcess.Start()) {
            throw 'Failed to start published Todo.Api.dll.'
        }
        $apiProcess.BeginOutputReadLine()
        $apiProcess.BeginErrorReadLine()

        $deadline = [DateTime]::UtcNow + [TimeSpan]::FromMinutes(1)
        while ([DateTime]::UtcNow -lt $deadline -and -not $listenState.Url) {
            if ($apiProcess.HasExited) {
                throw "Published API exited before listening (code $($apiProcess.ExitCode)). captured=$($listenState.Log)"
            }
            Start-Sleep -Milliseconds 100
        }

        $listenUrl = $listenState.Url
        if (-not $listenUrl) {
            throw "Timed out waiting for published Kestrel listen URL. captured=$($listenState.Log)"
        }

        Write-Host "Published API listening at $listenUrl"
        Wait-HttpOk -Url "$listenUrl/health" -Timeout ([TimeSpan]::FromMinutes(1))

        $root = Invoke-WebRequest -Uri "$listenUrl/" -Method GET -TimeoutSec 10
        if ([int]$root.StatusCode -ne 200) {
            throw "GET / returned $($root.StatusCode)"
        }
        $rootCt = [string]$root.Headers['Content-Type']
        if ($rootCt -notlike 'text/html*') {
            throw "GET / content type was '$rootCt', expected text/html."
        }
        $servedBy = [string]$root.Headers['X-Served-By']
        if ($servedBy -ne $replica) {
            throw "GET / X-Served-By was '$servedBy', expected '$replica'."
        }

        $assetMatches = [regex]::Matches([string]$root.Content, '(?:src|href)="(/assets/[^"]+)"')
        if ($assetMatches.Count -eq 0) {
            throw 'Published index.html contained no /assets/ URLs.'
        }
        foreach ($match in $assetMatches) {
            $assetUrl = $match.Groups[1].Value
            $asset = Invoke-WebRequest -Uri ($listenUrl + $assetUrl) -Method GET -TimeoutSec 10
            if ([int]$asset.StatusCode -ne 200) {
                throw "Asset $assetUrl returned $($asset.StatusCode)"
            }
        }

        # Assign TODO_BASE_URL inside the restore try (F47); do not Push-Location for npm --prefix.
        $hadTodoBaseUrl = Test-Path Env:TODO_BASE_URL
        $previousTodoBaseUrl = if ($hadTodoBaseUrl) { $env:TODO_BASE_URL } else { $null }
        try {
            $env:TODO_BASE_URL = $listenUrl
            npm --prefix $webDir run test:e2e
            if ($LASTEXITCODE -ne 0) {
                throw "npm run test:e2e failed with exit code $LASTEXITCODE"
            }
        }
        finally {
            if ($hadTodoBaseUrl) {
                $env:TODO_BASE_URL = $previousTodoBaseUrl
            }
            else {
                Remove-Item Env:TODO_BASE_URL -ErrorAction SilentlyContinue
            }
        }
    }
    catch {
        $runError = $_
    }
    finally {
        try {
            if ($null -ne $apiProcess -and -not $apiProcess.HasExited) {
                Write-Host "Stopping published API PID $($apiProcess.Id)"
                $apiProcess.Kill($true)
                [void]$apiProcess.WaitForExit(15000)
            }
        }
        catch {
            if ($null -eq $cleanupError) { $cleanupError = $_ }
        }
        finally {
            if ($null -ne $apiProcess) { $apiProcess.Dispose() }
        }

        foreach ($name in @($pgName, $redisName)) {
            try {
                docker rm -f $name 2>$null | Out-Null
            }
            catch {
                if ($null -eq $cleanupError) { $cleanupError = $_ }
            }
        }
    }

    if ($null -ne $cleanupError -and $null -ne $runError) {
        throw "PublishedWeb failed with run error and cleanup failure.`nRun error: $runError`nCleanup error: $cleanupError"
    }
    if ($null -ne $cleanupError) {
        throw "PublishedWeb cleanup failed: $cleanupError"
    }
    if ($null -ne $runError) {
        throw $runError
    }
}

if ($Target -eq 'PublishedWeb') {
    Invoke-PublishedWeb
}
else {
    Invoke-AspireTargets
}

Write-Host "verify.ps1 -Target $Target succeeded"
