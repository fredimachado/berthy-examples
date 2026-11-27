#Requires -Version 7
param(
    [Parameter(Mandatory = $true)]
    [string] $ApiUrl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$base = $ApiUrl.TrimEnd('/')
$versionUrl = "$base/version"

$response = Invoke-WebRequest -Uri $versionUrl -Method GET
if ($response.StatusCode -ne 200) {
    throw "GET /version returned $($response.StatusCode)"
}

$payload = $response.Content | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace([string]$payload.version)) {
    throw "GET /version missing version field: $($response.Content)"
}
if ([string]::IsNullOrWhiteSpace([string]$payload.commit)) {
    throw "GET /version missing commit field: $($response.Content)"
}

Write-Host "smoke ok: version=$($payload.version) commit=$($payload.commit)"
