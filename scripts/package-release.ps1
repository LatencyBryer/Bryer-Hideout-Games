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
    [ValidatePattern('^\d+\.\d+\.\d+(?:\.\d+)?$')][string]$Version = ''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent $PSScriptRoot
$pluginProject = Join-Path $root 'BryersHideoutGames.csproj'
$hostProject = Join-Path $root 'Host\BryersHideoutGamesHost.csproj'
# Read the authoritative version from the project. Do not silently turn 1.0.0.1
# into 1.0.0.1.0, or republish a package at the previous assembly version.
[xml]$projectXml = Get-Content -LiteralPath $pluginProject -Raw
$properties = $projectXml.Project.PropertyGroup
$sourceVersion = [string]$properties.AssemblyVersion
if ($sourceVersion -notmatch '^\d+\.\d+\.\d+\.\d+$') {
    throw "BryersHideoutGames.csproj must declare a four-part <AssemblyVersion>: $sourceVersion"
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $assembledVersion = $sourceVersion
} elseif ($Version -match '^\d+\.\d+\.\d+$') {
    $assembledVersion = "$Version.0"
} else {
    $assembledVersion = $Version
}
foreach ($name in @('Version', 'AssemblyVersion', 'FileVersion')) {
    $value = [string]$properties.$name
    if ($value -ne $assembledVersion) {
        throw "$name in BryersHideoutGames.csproj is '$value', but the requested Release is '$assembledVersion'. Update all three values before building."
    }
}
$manifestPath = Join-Path $root 'BryersHideoutGames.json'
$sourceManifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
if ([string]$sourceManifest.AssemblyVersion -ne $assembledVersion) {
    throw "BryersHideoutGames.json version is '$($sourceManifest.AssemblyVersion)' but the project is '$assembledVersion'. Update the JSON before building."
}
Write-Host "Building manual Dalamud Release $assembledVersion" -ForegroundColor Cyan
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
        -p:Version=$assembledVersion -p:AssemblyVersion=$assembledVersion -p:FileVersion=$assembledVersion -o $pluginOut
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
$manifest = $sourceManifest
$manifest.AssemblyVersion = $assembledVersion
[IO.File]::WriteAllText((Join-Path $package 'BryersHideoutGames.json'), ($manifest | ConvertTo-Json -Depth 20), [Text.UTF8Encoding]::new($false))
$hostInputs = @(Get-ChildItem -LiteralPath (Join-Path $root 'Host') -File -Filter '*.cs') + @(Get-Item $hostProject)
$hostStamp = ($hostInputs | Sort-Object FullName | ForEach-Object {
    $_.FullName.Substring($root.Length) + ':' + (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
}) -join "`n"
# Windows PowerShell 5.1 runs on .NET Framework, which has neither SHA256.HashData
# nor Convert.ToHexString. ComputeHash/BitConverter work on both PS 5.1 and 7+.
$sha256 = [Security.Cryptography.SHA256]::Create()
try {
    $sha = $sha256.ComputeHash([Text.Encoding]::UTF8.GetBytes($hostStamp))
} finally {
    $sha256.Dispose()
}
$shortHash = ([BitConverter]::ToString($sha).Replace('-', '')).Substring(0,12).ToLowerInvariant()
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
# ZipFile lives in this extra assembly on Windows PowerShell 5.1 / .NET Framework.
Add-Type -AssemblyName System.IO.Compression.FileSystem
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
    $pluginManifestEntry = $archive.GetEntry('BryersHideoutGames.json')
    $manifestReader = [IO.StreamReader]::new($pluginManifestEntry.Open())
    try { $packageManifest = $manifestReader.ReadToEnd() | ConvertFrom-Json }
    finally { $manifestReader.Dispose() }
    if ([string]$packageManifest.AssemblyVersion -ne $assembledVersion) {
        throw "Release ZIP manifest version does not match $assembledVersion."
    }
} finally { $archive.Dispose() }

# Prepare the Dalamud custom repository metadata for a MANUAL commit and push.
# This script never connects to GitHub, creates tags, or uploads anything.
$repoPath = Join-Path $root 'repo.json'
[array]$repoEntries = @(Get-Content -LiteralPath $repoPath -Raw | ConvertFrom-Json)
if ($repoEntries.Count -ne 1 -or [string]$repoEntries[0].InternalName -ne 'BryersHideoutGames') {
    throw 'Expected one BryersHideoutGames entry in repo.json.'
}
foreach ($link in @('DownloadLinkInstall', 'DownloadLinkUpdate', 'DownloadLinkTesting')) {
    $uri = [string]$repoEntries[0].$link
    if (!$uri.EndsWith('/releases/latest/download/' + $assetName, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Unexpected $link in repo.json: $uri"
    }
}
# Some manually maintained repo.json files omit these optional metadata fields.
# In PowerShell with StrictMode Latest, assigning a missing PSCustomObject property
# throws PropertyAssignmentException. Add missing fields instead of failing after
# the Release ZIP has already been compiled and validated.
$repoEntry = $repoEntries[0]
$repoUpdates = @{
    AssemblyVersion = $assembledVersion
    LastUpdate = [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()
}
foreach ($field in $repoUpdates.Keys) {
    $property = $repoEntry.PSObject.Properties[$field]
    if ($null -eq $property) {
        $repoEntry | Add-Member -MemberType NoteProperty -Name $field -Value $repoUpdates[$field]
    } else {
        $property.Value = $repoUpdates[$field]
    }
}
[IO.File]::WriteAllText($repoPath, (ConvertTo-Json -InputObject $repoEntries -Depth 30) + "`n", [Text.UTF8Encoding]::new($false))

Write-Host "Validated installable Dalamud Release: $zipPath" -ForegroundColor Green
Write-Host "Bundled Chromium: $runtimeName" -ForegroundColor Green
Write-Host "Updated repo.json to $assembledVersion (commit this file manually)." -ForegroundColor Yellow
Write-Host "Manual upload: create a new GitHub Release, attach $assetName, and mark it Latest." -ForegroundColor Yellow
Write-Host 'Do NOT attach the repository Source code (zip) as the plugin installable.' -ForegroundColor Yellow
