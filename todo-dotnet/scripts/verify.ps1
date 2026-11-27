#Requires -Version 7
param(
    [Parameter(Mandatory = $true)]
    [ValidateSet('Api')]
    [string] $Target
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$todoAppHost = (Resolve-Path (Join-Path $repoRoot 'todo-dotnet\AppHost\apphost.cs')).Path
$smokeScript = Join-Path $PSScriptRoot 'smoke.ps1'

function Normalize-AppHostPath([string] $path) {
    return [System.IO.Path]::GetFullPath($path).TrimEnd('\', '/').ToLowerInvariant()
}

function Find-AppHostPid([string] $canonicalPath) {
    $wanted = Normalize-AppHostPath $canonicalPath
    $psJson = aspire ps --format Json --non-interactive --nologo | Out-String
    $entries = @(if ([string]::IsNullOrWhiteSpace($psJson)) { @() } else { $psJson | ConvertFrom-Json })
    $matches = @($entries | Where-Object {
        $_.appHostPath -and (Normalize-AppHostPath ([string]$_.appHostPath)) -eq $wanted
    })

    if ($matches.Count -ne 1) {
        throw "Expected exactly one AppHost for $canonicalPath, found $($matches.Count)."
    }

    $pidValue = [int]$matches[0].appHostPid
    if ($pidValue -le 0) {
        throw "AppHost PID must be positive; got $pidValue."
    }

    return $pidValue
}

function Get-ApiHttpUrl([string] $apphost) {
    $describeJson = aspire describe api --format Json --apphost $apphost --non-interactive --nologo | Out-String
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

try {
    Write-Host "Starting AppHost: $todoAppHost"
    aspire start --apphost $todoAppHost --isolated --non-interactive --format Json --nologo | Out-Host

    $todoAppHostPid = Find-AppHostPid $todoAppHost
    Write-Host "Owned AppHost PID: $todoAppHostPid"

    aspire wait api --status healthy --apphost $todoAppHost --non-interactive --nologo | Out-Host

    $apiUrl = Get-ApiHttpUrl $todoAppHost
    Write-Host "API http endpoint: $apiUrl"

    & $smokeScript -ApiUrl $apiUrl
    if ($LASTEXITCODE -ne 0) {
        throw "smoke.ps1 failed with exit code $LASTEXITCODE"
    }

    Write-Host "verify.ps1 -Target $Target succeeded"
}
finally {
    if ($null -eq $todoAppHostPid) {
        try {
            $todoAppHostPid = Find-AppHostPid $todoAppHost
        }
        catch {
            Write-Warning "Could not recover AppHost PID for cleanup: $_"
        }
    }

    if ($null -ne $todoAppHostPid) {
        Write-Host "Stopping AppHost PID $todoAppHostPid"
        aspire stop --pid $todoAppHostPid --apphost $todoAppHost --non-interactive --nologo | Out-Host
    }
}
