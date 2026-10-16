# Builds the release zips into dist\:
#   VamScanCache-<version>.zip              BepInEx + plugin, unzip into the VaM folder
#   VamScanCache-<version>-plugin-only.zip  just the plugin, for people who already use BepInEx
#
# Compiling the plugin needs VaM's own assemblies, so this runs on a machine with VaM
# installed: .\release\build-release.ps1 -VamDir "C:\path\to\VaM"
# None of VaM's files end up in the zips.
param(
    [Parameter(Mandatory = $true, HelpMessage = 'Your VaM folder, the one that contains VaM.exe')]
    [string]$VamDir
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$dist = Join-Path $root 'dist'

# Pinned BepInEx release; the hash makes sure every build bundles exactly this file.
$bepInExUrl = 'https://github.com/BepInEx/BepInEx/releases/download/v5.4.23.2/BepInEx_win_x64_5.4.23.2.zip'
$bepInExSha256 = 'f752ce4e838f4c305b9da1404b6745f2cff23b8bfd494f79f0c84d0a01f59b46'

if (-not (Test-Path (Join-Path $VamDir 'VaM_Data\Managed\Assembly-CSharp.dll'))) {
    throw "VaM's assemblies weren't found under $VamDir. Pass -VamDir with your VaM folder."
}

$pluginSource = Get-Content (Join-Path $root 'VamScanCache\Plugin.cs') -Raw
$version = [regex]::Match($pluginSource, 'BepInPlugin\(Guid, "[^"]+", "([^"]+)"\)').Groups[1].Value
if (-not $version) {
    throw 'Could not read the plugin version from VamScanCache\Plugin.cs'
}
Write-Host "Building release $version"

Remove-Item -Recurse -Force $dist -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $dist 'download') | Out-Null

# BepInEx
$bepZip = Join-Path $dist 'download\BepInEx.zip'
Invoke-WebRequest -Uri $bepInExUrl -OutFile $bepZip
$hash = (Get-FileHash $bepZip -Algorithm SHA256).Hash.ToLowerInvariant()
if ($hash -ne $bepInExSha256) {
    throw "BepInEx download hash mismatch: got $hash, expected $bepInExSha256"
}
$bepDir = Join-Path $dist 'download\BepInEx'
Expand-Archive $bepZip -DestinationPath $bepDir

# Plugin, compiled against the pinned BepInEx rather than whatever is installed in VaM.
dotnet build (Join-Path $root 'VamScanCache') -c Release "-p:VamDir=$VamDir" "-p:BepInExDir=$bepDir\BepInEx\core"
if ($LASTEXITCODE -ne 0) { throw 'Plugin build failed' }
$pluginDll = Join-Path $root 'VamScanCache\bin\Release\net35\VamScanCache.dll'

# Full zip: BepInEx + plugin
$full = Join-Path $dist 'stage\full'
Copy-Item -Recurse $bepDir $full
New-Item -ItemType Directory -Force (Join-Path $full 'BepInEx\plugins'), (Join-Path $full 'LICENSES') | Out-Null
Copy-Item $pluginDll (Join-Path $full 'BepInEx\plugins')
Copy-Item (Join-Path $PSScriptRoot 'INSTALL-VamScanCache.txt') (Join-Path $full 'INSTALL-VamScanCache.txt')
Copy-Item (Join-Path $root 'licenses\BepInEx-LICENSE.txt') (Join-Path $full 'LICENSES')
Compress-Archive -Path (Join-Path $full '*') -DestinationPath (Join-Path $dist "VamScanCache-$version.zip")

# Plugin-only zip
$only = Join-Path $dist 'stage\plugin-only'
New-Item -ItemType Directory -Force (Join-Path $only 'BepInEx\plugins') | Out-Null
Copy-Item $pluginDll (Join-Path $only 'BepInEx\plugins')
Copy-Item (Join-Path $PSScriptRoot 'INSTALL-VamScanCache.txt') $only
Compress-Archive -Path (Join-Path $only '*') -DestinationPath (Join-Path $dist "VamScanCache-$version-plugin-only.zip")

Remove-Item -Recurse -Force (Join-Path $dist 'stage'), (Join-Path $dist 'download')
Get-ChildItem $dist | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
