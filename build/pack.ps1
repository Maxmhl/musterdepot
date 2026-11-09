<#
.SYNOPSIS
  Baut lokal den Musterdepot-Installer: dotnet publish (App + Brücke, win-x64, self-contained, ReadyToRun) und vpk pack.

.EXAMPLE
  ./build/pack.ps1 -Version 0.2.0
  ./build/pack.ps1 -Version 0.3.0-beta.1 -Channel beta
  ./build/pack.ps1 -Version 0.2.1 -RepoUrl https://github.com/OWNER/musterdepot   # mit Delta gegen letzte Version

.NOTES
  Voraussetzungen: Windows, .NET 10 SDK, vpk (dotnet tool install -g vpk; Version passend zum Velopack-NuGet der App).
  Ergebnis in -OutputDir (Standard: Releases): Musterdepot-<channel>-Setup.exe, Portable.zip, *.nupkg, releases.<channel>.json.
  Hochladen macht die Release-Pipeline (.github/workflows/release.yml), lokal nur bei Bedarf mit -Upload.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?$')]
    [string] $Version,
    [string] $Channel = 'win',
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',
    [string] $PublishDir = 'artifacts/publish',
    [string] $OutputDir = 'Releases',
    [string] $PackId = 'Musterdepot',
    # Optional: GitHub-Repository für Delta-Pakete (vpk download) und -Upload.
    [string] $RepoUrl,
    # Optional: Token (sonst $env:GITHUB_TOKEN).
    [string] $Token = $env:GITHUB_TOKEN,
    [switch] $SkipPublish,
    [switch] $Upload
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
Push-Location $root
try {
    if (-not (Get-Command vpk -ErrorAction SilentlyContinue)) {
        throw "vpk nicht gefunden. Installieren: dotnet tool install -g vpk"
    }

    if (-not $SkipPublish) {
        if (Test-Path $PublishDir) { Remove-Item $PublishDir -Recurse -Force }
        foreach ($project in 'src/Musterdepot.App/Musterdepot.App.csproj', 'src/Musterdepot.McpBridge/Musterdepot.McpBridge.csproj') {
            Write-Host "==> publish $project" -ForegroundColor Cyan
            dotnet publish $project --configuration $Configuration --runtime $Runtime --self-contained true `
                -p:PublishReadyToRun=true -p:Version=$Version --output $PublishDir
            if ($LASTEXITCODE -ne 0) { throw "dotnet publish $project fehlgeschlagen" }
        }
    }
    foreach ($exe in 'Musterdepot.exe', 'Musterdepot.McpBridge.exe') {
        if (-not (Test-Path (Join-Path $PublishDir $exe))) { throw "$exe fehlt in $PublishDir" }
    }

    if ($RepoUrl) {
        Write-Host "==> vorherige Version für Delta laden" -ForegroundColor Cyan
        $dl = @('download', 'github', '--repoUrl', $RepoUrl, '--channel', $Channel, '--outputDir', $OutputDir)
        if ($Token) { $dl += @('--token', $Token) }
        vpk @dl
        if ($LASTEXITCODE -ne 0) { Write-Warning "Keine vorherige Version geladen – es entsteht nur ein Vollpaket." }
    }

    Write-Host "==> vpk pack $PackId $Version ($Channel)" -ForegroundColor Cyan
    vpk pack --packId $PackId --packVersion $Version --packDir $PublishDir --mainExe Musterdepot.exe `
        --packTitle Musterdepot --packAuthors Musterdepot --channel $Channel --outputDir $OutputDir
    if ($LASTEXITCODE -ne 0) { throw "vpk pack fehlgeschlagen" }

    if ($Upload) {
        if (-not $RepoUrl -or -not $Token) { throw "-Upload braucht -RepoUrl und ein Token" }
        $up = @('upload', 'github', '--repoUrl', $RepoUrl, '--channel', $Channel, '--outputDir', $OutputDir,
            '--tag', "v$Version", '--releaseName', "Musterdepot $Version", '--publish', '--token', $Token)
        if ($Version -match '-') { $up += '--pre' }
        vpk @up
        if ($LASTEXITCODE -ne 0) { throw "vpk upload fehlgeschlagen" }
    }

    Write-Host "Fertig: $(Resolve-Path $OutputDir)" -ForegroundColor Green
    Get-ChildItem $OutputDir | Format-Table Name, Length
}
finally {
    Pop-Location
}
