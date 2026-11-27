#Requires -Version 7
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Api')]
    [string] $Target
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$todoAppHost = (Resolve-Path (Join-Path $repoRoot 'todo-dotnet\AppHost\apphost.cs')).Path
$smokeScript = Join-Path $PSScriptRoot 'smoke.ps1'

function Normalize-AppHostPath([string] $path) {
    return [System.IO.Path]::GetFullPath($path).TrimEnd('\', '/').ToLowerInvariant()
}

function Assert-NativeExit([string] $commandName) {
    if ($LASTEXITCODE -ne 0) {
        throw "$commandName exited with code $LASTEXITCODE."
    }
}

function Get-ApiHttpUrl([string] $apphost) {
    $describeJson = aspire describe api --format Json --apphost $apphost --non-interactive --nologo | Out-String
    Assert-NativeExit 'aspire describe'
    $snapshot = $describeJson | ConvertFrom-Json
    $resources = @()
    if ($snapshot.resources) {
        $resources = @($snapshot.resources)
    }
    elseif ($snapshot.name -or $snapshot.displayName) {
        $resources = @($snapshot)
    }

    # Isolated starts suffix the resource name (api-xxxxxx); displayName stays "api".
    $resource = $resources | Where-Object {
        [string]$_.displayName -eq 'api' -or [string]$_.name -eq 'api' -or [string]$_.name -like 'api-*'
    } | Select-Object -First 1

    if (-not $resource) {
        throw "aspire describe api returned no api resource: $describeJson"
    }

    $urls = @($resource.urls)
    $http = $urls | Where-Object { [string]$_.name -eq 'http' } | Select-Object -First 1
    if (-not $http) {
        $http = $urls | Where-Object { [string]$_.url -like 'http://*' } | Select-Object -First 1
    }

    if (-not $http) {
        throw "api resource has no http endpoint: $describeJson"
    }

    $apiUrl = [string]$http.url
    if ([string]::IsNullOrWhiteSpace($apiUrl)) {
        throw "api http endpoint URL is empty: $describeJson"
    }

    return $apiUrl.TrimEnd('/')
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

    $todoAppHostPid = [int]$started.appHostPid
    if ($todoAppHostPid -le 0) {
        throw "aspire start returned non-positive appHostPid: $($started.appHostPid)"
    }
    Write-Host "Owned AppHost PID: $todoAppHostPid"

    aspire wait api --status healthy --apphost $todoAppHost --non-interactive --nologo | Out-Host
    Assert-NativeExit 'aspire wait'

    $apiUrl = Get-ApiHttpUrl $todoAppHost
    Write-Host "API http endpoint: $apiUrl"

    & $smokeScript -ApiUrl $apiUrl
    if ($LASTEXITCODE -ne 0) {
        throw "smoke.ps1 failed with exit code $LASTEXITCODE"
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

if ($null -ne $cleanupError) {
    throw "AppHost cleanup failed: $cleanupError"
}

if ($null -ne $runError) {
    throw $runError
}

Write-Host "verify.ps1 -Target $Target succeeded"
