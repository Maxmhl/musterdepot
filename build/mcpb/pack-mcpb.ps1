<#
.SYNOPSIS
  Packt die Claude-Desktop-Erweiterung (.mcpb) mit der Brücken-Exe.

.DESCRIPTION
  1. Kopiert den Inhalt von src/Musterdepot.McpBridge/mcpb/ (manifest.json, Icon …, angelegt von WS3b) in einen Staging-Ordner.
  2. Veröffentlicht Musterdepot.McpBridge als Einzeldatei (win-x64, self-contained) nach <staging>/server/.
  3. Setzt "version" im Manifest auf -Version und ruft `mcpb pack` (npm-Paket @anthropic-ai/mcpb) auf.
  Das Manifest muss als Einstiegspunkt server/Musterdepot.McpBridge.exe verwenden (siehe build/mcpb/README.md).

.EXAMPLE
  ./build/mcpb/pack-mcpb.ps1 -Version 0.2.0
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)] [string] $Version,
    [string] $OutputDir = 'artifacts/mcpb',
    [string] $Runtime = 'win-x64',
    [string] $Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $root
try {
    $source = 'src/Musterdepot.McpBridge/mcpb'
    if (-not (Test-Path "$source/manifest.json")) { throw "$source/manifest.json fehlt (wird von Workstream 3b angelegt)." }
    if (-not (Get-Command npx -ErrorAction SilentlyContinue)) { throw "npx (Node.js) wird für mcpb benötigt." }

    $staging = Join-Path 'artifacts' 'mcpb-staging'
    if (Test-Path $staging) { Remove-Item $staging -Recurse -Force }
    New-Item -ItemType Directory -Path $staging | Out-Null
    Copy-Item "$source/*" $staging -Recurse

    dotnet publish src/Musterdepot.McpBridge/Musterdepot.McpBridge.csproj --configuration $Configuration --runtime $Runtime `
        --self-contained true -p:PublishSingleFile=true -p:Version=$Version --output (Join-Path $staging 'server')
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish der Brücke fehlgeschlagen" }

    $manifestPath = Join-Path $staging 'manifest.json'
    $manifest = Get-Content $manifestPath -Raw | ConvertFrom-Json
    $manifest.version = $Version
    $manifest | ConvertTo-Json -Depth 32 | Set-Content $manifestPath -Encoding utf8NoBOM

    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
    $out = Join-Path $OutputDir "Musterdepot-$Version.mcpb"
    npx --yes @anthropic-ai/mcpb validate $manifestPath
    if ($LASTEXITCODE -ne 0) { throw "mcpb validate fehlgeschlagen" }
    npx --yes @anthropic-ai/mcpb pack $staging $out
    if ($LASTEXITCODE -ne 0) { throw "mcpb pack fehlgeschlagen" }
    Write-Host "Fertig: $out" -ForegroundColor Green
}
finally {
    Pop-Location
}
