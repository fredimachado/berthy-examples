#Requires -Version 7
param(
    [Parameter(Mandatory = $true)]
    [string] $ApiUrl
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$base = $ApiUrl.TrimEnd('/')
$versionUrl = "$base/version"
$todosUrl = "$base/api/todos"

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

$todosResponse = Invoke-WebRequest -Uri $todosUrl -Method GET
if ($todosResponse.StatusCode -ne 200) {
    throw "GET /api/todos returned $($todosResponse.StatusCode)"
}

$todosContent = [string]$todosResponse.Content
if (-not $todosContent.TrimStart().StartsWith('[')) {
    throw "GET /api/todos expected a JSON array: $todosContent"
}

Write-Host "smoke ok: version=$($payload.version) commit=$($payload.commit) todos=$todosContent"
