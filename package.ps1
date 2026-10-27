# Builds the plugin, produces one installable zip per supported Jellyfin version in .\dist,
# and updates manifest.json (the plugin repository manifest).
#   dist\jellyfin-12\Completionist_<version>\      (Jellyfin 12.x,  .NET 10)
#   dist\jellyfin-10.11\Completionist_<version>\   (Jellyfin 10.11, .NET 9)
$ErrorActionPreference = 'Stop'

# Each Jellyfin line gets its own last version number (.12 / .11). A Jellyfin 12 server can also
# install 10.11 builds, so equal version numbers would leave it to pick between the two builds.
$baseVersion = '1.0.0'
$changelog = 'First release: finds missing TV seasons and episodes (TVmaze or TMDb) and missing films from your collections (TMDb), and posts new ones to Discord.'
$repo = 'TNT24/jellyfin-completionist'
$project = Join-Path $PSScriptRoot 'Jellyfin.Plugin.Completionist'
$tests = Join-Path $PSScriptRoot 'tests\Jellyfin.Plugin.Completionist.Tests'
$dist = Join-Path $PSScriptRoot 'dist'
$targets = @(
    @{ Target = '12';    TargetAbi = '12.0.0.0';  Label = 'jellyfin-12';    Build = '12' },
    @{ Target = '10.11'; TargetAbi = '10.11.0.0'; Label = 'jellyfin-10.11'; Build = '11' }
)
$name = 'Completionist'
$description = 'Lists aired TV seasons and episodes, and released films from your collections, that are missing from your library, and posts new ones to Discord.'
$overview = 'Find missing episodes, seasons and collection films, with Discord notifications'
$category = 'General'
$guid = 'aceeaf3d-1c56-410f-af3b-0f4f8fa40746'
$timestamp = (Get-Date).ToUniversalTime().ToString('yyyy-MM-ddTHH:mm:ssZ')
# Must be UTF-8 *without* a BOM: Jellyfin parses meta.json from raw bytes and rejects a BOM.
# (Out-File -Encoding utf8 in Windows PowerShell always writes one.)
$utf8NoBom = New-Object Text.UTF8Encoding $false
$manifestVersions = @()

# Test each Jellyfin line before packaging it.
foreach ($t in $targets) {
    dotnet test $tests -c Release "-p:JellyfinTarget=$($t.Target)"
    if ($LASTEXITCODE -ne 0) { throw "Tests failed for Jellyfin $($t.Target)" }
}

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory $dist | Out-Null

foreach ($t in $targets) {
    $version = "$baseVersion.$($t.Build)"
    $dir = Join-Path $dist $t.Label
    $staging = Join-Path $dir 'publish'
    dotnet publish $project -c Release -o $staging "-p:JellyfinTarget=$($t.Target)" "-p:AssemblyVersion=$version" "-p:FileVersion=$version"
    if ($LASTEXITCODE -ne 0) { throw "Build failed for Jellyfin $($t.Target)" }

    # Drop-in folder: copy "Completionist_<version>" straight into the server's plugins directory.
    $out = Join-Path $dir "Completionist_$version"
    New-Item -ItemType Directory $out | Out-Null
    Copy-Item (Join-Path $staging 'Jellyfin.Plugin.Completionist.dll') $out
    Copy-Item (Join-Path $PSScriptRoot 'assets\logo.png') $out
    Remove-Item $staging -Recurse -Force

    $meta = [ordered]@{
        category    = $category
        changelog   = $changelog
        description = $description
        guid        = $guid
        name        = $name
        overview    = $overview
        owner       = 'TNT24'
        targetAbi   = $t.TargetAbi
        timestamp   = $timestamp
        version     = $version
        status      = 'Active'
        # Jellyfin keeps this value from the zip when installing, so false would turn off automatic updates.
        autoUpdate  = $true
        imagePath   = 'logo.png'
        assemblies  = @()
    }
    [IO.File]::WriteAllText((Join-Path $out 'meta.json'), ($meta | ConvertTo-Json), $utf8NoBom)

    # bsdtar (built into Windows 10+) writes portable '/' paths; PowerShell 5's Compress-Archive writes '\',
    # which Linux/Docker servers unpack as a single odd file name. The files go at the root of the zip,
    # because Jellyfin unpacks a repository install straight into plugins/<name>_<version> and expects meta.json there.
    $zipName = "Completionist_$($version)_$($t.Label).zip"
    $zip = Join-Path $dist $zipName
    & "$env:SystemRoot\System32\tar.exe" -a -cf $zip -C $out 'Jellyfin.Plugin.Completionist.dll' 'logo.png' 'meta.json'
    if ($LASTEXITCODE -ne 0) { throw "Zipping failed for Jellyfin $($t.Target)" }
    Write-Host "Created $out"
    Write-Host "Created $zip"

    $manifestVersions += [ordered]@{
        version   = $version
        changelog = $changelog
        targetAbi = $t.TargetAbi
        sourceUrl = "https://github.com/$repo/releases/download/v$baseVersion/$zipName"
        checksum  = (Get-FileHash $zip -Algorithm MD5).Hash.ToLowerInvariant()
        timestamp = $timestamp
    }
}

# Plugin repository manifest, served from the repo's main branch. Versions from earlier releases are
# kept so they stay installable; entries for the versions being built are replaced. The 12 build is
# listed first, because Jellyfin keeps the first compatible entry.
$manifestPath = Join-Path $PSScriptRoot 'manifest.json'
$oldVersions = @()
if (Test-Path $manifestPath) {
    $old = @(Get-Content $manifestPath -Raw | ConvertFrom-Json)
    $builtVersions = @($manifestVersions | ForEach-Object { $_.version })
    $oldVersions = @($old[0].versions | Where-Object { $builtVersions -notcontains $_.version })
}
$manifest = @(
    [ordered]@{
        guid        = $guid
        name        = $name
        description = $description
        overview    = $overview
        owner       = 'TNT24'
        category    = $category
        imageUrl    = "https://raw.githubusercontent.com/$repo/main/assets/logo.png"
        versions    = @($manifestVersions) + $oldVersions
    }
)
[IO.File]::WriteAllText($manifestPath, (ConvertTo-Json -InputObject $manifest -Depth 5), $utf8NoBom)
Write-Host "Updated $manifestPath"
