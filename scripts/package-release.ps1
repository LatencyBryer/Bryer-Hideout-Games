<#
/* ████  ████  █   █ █████ ████  © */
/* █░░░█ █░░░█  █ █ ░█░░░░░█░░░█   */
/* ████░░████░░  █ ░ ████░░████░░  */
/* █░░░█ █░░█░ ░ █░ ░█░░░░ █░░█░ ░ */
/* ████░░█░░░█░  █░░ █████░█░░░█░  */
/* ░░░░ ░░░  ░   ░░  ░░░░░ ░░  ░   */
/* ░░░░  ░   ░   ░   ░░░░░ ░   ░   */
#>

param(
    [Parameter(Mandatory = $true)][ValidatePattern('^\d+\.\d+\.\d+$')][string]$Version
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $root 'BryersHideoutGames.csproj'
$hostProject = Join-Path $root 'Host\BryersHideoutGamesHost.csproj'
$assembledVersion = "$Version.0"
$releaseFolder = Join-Path $root '_release'
$pluginOut = Join-Path $releaseFolder 'plugin'
$hostOut = Join-Path $releaseFolder 'host'
$package = Join-Path $releaseFolder 'package'
$assetName = 'BryersHideoutGames-latest.zip'
$zipPath = Join-Path $releaseFolder $assetName
if (!(Get-Command dotnet -ErrorAction SilentlyContinue)) { throw '.NET 10 SDK is required.' }
if (Test-Path -LiteralPath $releaseFolder) { Remove-Item -LiteralPath $releaseFolder -Recurse -Force }
New-Item -ItemType Directory -Path $pluginOut,$hostOut,$package -Force | Out-Null
Push-Location $root
try {
    & dotnet restore $pluginProject -r win-x64
    if ($LASTEXITCODE -ne 0) { throw 'Plugin restore failed.' }
    & dotnet build $pluginProject -c Release -p:Platform=x64 --no-restore `
        -p:Version=$Version -p:AssemblyVersion=$assembledVersion -p:FileVersion=$assembledVersion -o $pluginOut
    if ($LASTEXITCODE -ne 0) { throw 'Plugin compilation failed.' }
    & dotnet publish $hostProject -c Release -r win-x64 --self-contained false -p:Platform=x64 -o $hostOut
    if ($LASTEXITCODE -ne 0) { throw 'Chromium host publish failed.' }
} finally { Pop-Location }
foreach ($f in @('BryersHideoutGames.dll','BryersHideoutGames.deps.json')) {
    $source = Join-Path $pluginOut $f
    if (!(Test-Path -LiteralPath $source)) { throw "Compiled plugin file missing: $source" }
    Copy-Item -LiteralPath $source -Destination $package
}
$dpapi = Join-Path $pluginOut 'System.Security.Cryptography.ProtectedData.dll'
if (Test-Path -LiteralPath $dpapi) { Copy-Item -LiteralPath $dpapi -Destination $package }
Copy-Item -LiteralPath (Join-Path $root 'Resources\minigame-icons.bgra') -Destination $package
Copy-Item -LiteralPath (Join-Path $root 'icon.png') -Destination $package
Copy-Item -LiteralPath (Join-Path $root 'THIRD_PARTY_NOTICES.md') -Destination $package
$manifest = Get-Content -LiteralPath (Join-Path $root 'BryersHideoutGames.json') -Raw | ConvertFrom-Json
$manifest.AssemblyVersion = $assembledVersion
$manifest | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $package 'BryersHideoutGames.json') -Encoding utf8
$hostInputs = @(Get-ChildItem -LiteralPath (Join-Path $root 'Host') -File -Filter '*.cs') + @(Get-Item $hostProject)
$hostStamp = ($hostInputs | Sort-Object FullName | ForEach-Object {
    $_.FullName.Substring($root.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}) -join "`n"
$sha = [Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($hostStamp))
$shortHash = ([Convert]::ToHexString($sha)).Substring(0,12).ToLowerInvariant()
$runtimeName = '_browser-runtime-v13-' + $shortHash
$runtimeDest = Join-Path $package $runtimeName
New-Item -ItemType Directory -Path $runtimeDest -Force | Out-Null
Copy-Item -Path (Join-Path $hostOut '*') -Destination $runtimeDest -Recurse -Force
foreach ($name in @('BryersHideoutGamesHost.exe','libcef.dll','CefSharp.Core.Runtime.dll','CefSharp.BrowserSubprocess.exe','locales\en-US.pak')) {
    if (!(Test-Path -LiteralPath (Join-Path $runtimeDest $name))) { throw "Chromium runtime missing $name" }
}
[IO.File]::WriteAllText((Join-Path $package '_browser-runtime-current.txt'), "$runtimeName`r`n", [Text.Encoding]::ASCII)
if (Test-Path -LiteralPath $zipPath) { Remove-Item -LiteralPath $zipPath -Force }
Add-Type -AssemblyName System.IO.Compression
[System.IO.Compression.ZipFile]::CreateFromDirectory($package, $zipPath, [IO.Compression.CompressionLevel]::Optimal, $false)

# Validate the final ARCHIVE, not only the staging directory. A source-only ZIP
# is installable by Dalamud but cannot open any game without these files.
$archive = [System.IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $archive.Entries) { [void]$names.Add($entry.FullName.Replace('\', '/')) }
    $required = @(
        'BryersHideoutGames.dll',
        'BryersHideoutGames.json',
        'BryersHideoutGames.deps.json',
        '_browser-runtime-current.txt',
        "$runtimeName/BryersHideoutGamesHost.exe",
        "$runtimeName/BryersHideoutGamesHost.dll",
        "$runtimeName/BryersHideoutGamesHost.runtimeconfig.json",
        "$runtimeName/libcef.dll",
        "$runtimeName/CefSharp.Core.Runtime.dll",
        "$runtimeName/CefSharp.BrowserSubprocess.exe",
        "$runtimeName/locales/en-US.pak"
    )
    foreach ($name in $required) {
        if (!$names.Contains($name)) { throw "Release ZIP is incomplete; missing entry: $name" }
    }
    $markerEntry = $archive.GetEntry('_browser-runtime-current.txt')
    $reader = [IO.StreamReader]::new($markerEntry.Open())
    try { $markerValue = $reader.ReadToEnd().Trim() } finally { $reader.Dispose() }
    if ($markerValue -cne $runtimeName) {
        throw "Release marker does not match bundled Chromium runtime: $markerValue"
    }
} finally { $archive.Dispose() }
Write-Host "Validated installable Dalamud Release: $zipPath" -ForegroundColor Green
Write-Host "Bundled Chromium: $runtimeName" -ForegroundColor Green
