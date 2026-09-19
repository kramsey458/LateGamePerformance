# Builds the mod and lays it out as an installable folder plus a zip under dist/.
#   .\build.ps1            build + package
#   .\build.ps1 -Install   also copy into Documents\Timberborn\Mods (close the game first)
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Timberborn",
    [switch]$Install
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$modName = "LateGamePerformance"

dotnet build "$root\source\$modName.csproj" -c Release -p:GameDir="$GameDir" --nologo -v q
if ($LASTEXITCODE -ne 0) { throw "Build failed." }

$dist = "$root\dist"
$modDir = "$dist\$modName"
if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }
New-Item -ItemType Directory -Force "$modDir\version-1.1\Scripts" | Out-Null
Copy-Item "$root\source\bin\Release\netstandard2.1\$modName.dll" "$modDir\version-1.1\Scripts\"
Copy-Item "$root\packaging\manifest.json" "$modDir\version-1.1\"
Copy-Item "$root\packaging\$modName.cfg" "$modDir\version-1.1\"
Copy-Item "$root\README.md", "$root\LICENSE" $modDir

$version = (Get-Content "$root\packaging\manifest.json" -Raw | ConvertFrom-Json).Version
$zip = "$dist\$modName-$version.zip"
# Entries are written one by one with forward-slash names. Under Windows PowerShell both Compress-Archive and
# ZipFile.CreateFromDirectory write backslash paths into the zip, which some tools extract wrongly.
Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
try {
    foreach ($file in Get-ChildItem $modDir -Recurse -File | Sort-Object FullName) {
        $entryName = "$modName/" + $file.FullName.Substring($modDir.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
} finally {
    $archive.Dispose()
}
if (-not (Get-ChildItem $modDir -Recurse -File)) { throw "Nothing was packaged." }
"Packaged: $zip"

if ($Install) {
    if (Get-Process Timberborn -ErrorAction SilentlyContinue) { throw "Timberborn is running. Close it, then install." }
    $target = Join-Path ([Environment]::GetFolderPath("MyDocuments")) "Timberborn\Mods\$modName"
    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    Copy-Item $modDir $target -Recurse
    "Installed: $target"
}
