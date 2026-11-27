#Requires -Version 7
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Api', 'AspireWeb')]
    [string] $Target
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

        $env:TODO_BASE_URL = $webUrl
        Push-Location $webDir
        try {
            npm --prefix $webDir run test:e2e
            if ($LASTEXITCODE -ne 0) {
                throw "npm run test:e2e failed with exit code $LASTEXITCODE"
            }
        }
        finally {
            Pop-Location
            Remove-Item Env:TODO_BASE_URL -ErrorAction SilentlyContinue
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

Write-Host "verify.ps1 -Target $Target succeeded"
