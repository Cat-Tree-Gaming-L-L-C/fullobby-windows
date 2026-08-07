<#
.SYNOPSIS
    Build and run the Fullobby mock API for client testing.
.DESCRIPTION
    Serves the full client contract (REST + SSE) with controllable in-memory state.
    Point the app at it with:  ./scripts/run-local.ps1 -ApiUrl http://localhost:<port>
.PARAMETER Port
    Port to listen on. Default 3000 (matches run-local's default).
.EXAMPLE
    ./scripts/run-mock.ps1
.EXAMPLE
    ./scripts/run-mock.ps1 -Port 5005
#>
[CmdletBinding()]
param([int]$Port = 3000)

$ErrorActionPreference = 'Stop'
$proj = Join-Path $PSScriptRoot '..\tools\Fullobby.MockApi\Fullobby.MockApi.csproj'

$env:ASPNETCORE_URLS = "http://localhost:$Port"
Write-Host ""
Write-Host "Mock API : http://localhost:$Port" -ForegroundColor Cyan
Write-Host "Control  : POST http://localhost:$Port/__mock/scenario/<name>  (see tools/Fullobby.MockApi/README.md)"
Write-Host "Point app: ./scripts/run-local.ps1 -ApiUrl http://localhost:$Port" -ForegroundColor Green
Write-Host "Ctrl+C to stop." -ForegroundColor DarkGray
Write-Host ""

dotnet run --project $proj -c Debug
