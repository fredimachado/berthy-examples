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
try {
    $todosJson = $todosContent | ConvertFrom-Json -NoEnumerate
} catch {
    throw "GET /api/todos returned non-JSON: $todosContent"
}

# ConvertFrom-Json turns JSON arrays into Object[]; a bare object would be PSCustomObject.
if ($null -eq $todosJson) {
    throw "GET /api/todos expected a JSON array, got null: $todosContent"
}
if ($todosJson -isnot [System.Array]) {
    throw "GET /api/todos expected a JSON array root, got $($todosJson.GetType().FullName): $todosContent"
}

$beforeCount = @($todosJson).Length

$createBody = '{"title":"  buy milk  "}'
$createResponse = Invoke-WebRequest -Uri $todosUrl -Method POST -Body $createBody -ContentType 'application/json'
if ($createResponse.StatusCode -ne 201) {
    throw "POST /api/todos returned $($createResponse.StatusCode): $($createResponse.Content)"
}

$created = $createResponse.Content | ConvertFrom-Json
$createdId = [long]$created.id
if ($createdId -lt 1) {
    throw "POST /api/todos missing positive id: $($createResponse.Content)"
}
if ([string]$created.title -ne 'buy milk') {
    throw "POST /api/todos expected trimmed title 'buy milk', got '$($created.title)'"
}
if ($created.done -ne $false) {
    throw "POST /api/todos expected done=false"
}

$location = [string]$createResponse.Headers['Location']
$expectedLocation = "/api/todos/$createdId"
if ($location -ne $expectedLocation) {
    throw "POST /api/todos Location='$location', want '$expectedLocation'"
}

$listAfterCreate = Invoke-WebRequest -Uri $todosUrl -Method GET
$listJson = ([string]$listAfterCreate.Content) | ConvertFrom-Json -NoEnumerate
$afterCreateCount = @($listJson).Length
if ($afterCreateCount -ne ($beforeCount + 1)) {
    throw "GET /api/todos after create expected $($beforeCount + 1) rows, got $afterCreateCount"
}

$listed = @($listJson) | Where-Object { [long]$_.id -eq $createdId } | Select-Object -First 1
if (-not $listed) {
    throw "GET /api/todos missing created id $createdId"
}
if ([string]$listed.title -ne 'buy milk') {
    throw "listed title='$($listed.title)', want 'buy milk'"
}

# Literal null → 422 title validation; must not write a second row.
$nullResponse = Invoke-WebRequest -Uri $todosUrl -Method POST -Body 'null' -ContentType 'application/json' -SkipHttpErrorCheck
if ([int]$nullResponse.StatusCode -ne 422) {
    throw "POST null status=$([int]$nullResponse.StatusCode), want 422"
}

$nullContentType = [string]$nullResponse.Headers['Content-Type']
if ($nullContentType -ne 'application/json') {
    throw "POST null Content-Type='$nullContentType', want 'application/json'"
}

$nullBody = [string]$nullResponse.Content
$nullPayload = $nullBody | ConvertFrom-Json
if ([string]$nullPayload.error -ne 'title must be 1 to 200 characters') {
    throw "POST null body=$nullBody"
}

$listFinal = Invoke-WebRequest -Uri $todosUrl -Method GET
$finalCount = @(([string]$listFinal.Content) | ConvertFrom-Json -NoEnumerate).Length
if ($finalCount -ne ($beforeCount + 1)) {
    throw "after POST null expected $($beforeCount + 1) rows, got $finalCount"
}

Write-Host "smoke ok: version=$($payload.version) commit=$($payload.commit) createdId=$createdId todos=$($listFinal.Content)"
