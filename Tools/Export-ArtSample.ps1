#requires -Version 7.0
[CmdletBinding()]
param([string]$BlenderPath = '')
$ErrorActionPreference='Stop'
$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
if (-not $BlenderPath) {
    $BlenderPath=Join-Path $projectRoot '.local/Tools/Blender/blender-5.2.1-windows-x64/blender.exe'
}
if (-not (Test-Path -LiteralPath $BlenderPath -PathType Leaf)) { throw 'Pass -BlenderPath pointing to an installed official Blender 5.2 LTS executable.' }
$editor = Get-CimInstance Win32_Process -Filter "Name='Unity.exe'" | Where-Object { $_.CommandLine -like '*GoldCamp.Unity*' }
if ($editor) { throw 'Finish the existing GoldCamp Editor task before exporting assets.' }
$scriptPath=Join-Path $projectRoot 'ArtSource/Sample01/generate.py'
& $BlenderPath --background --factory-startup --python $scriptPath -- --project $projectRoot
if ($LASTEXITCODE -ne 0) { throw "Blender export failed: $LASTEXITCODE" }
$manifest=Get-Content -LiteralPath (Join-Path $projectRoot 'ArtSource/Sample01/asset-manifest.json') -Raw | ConvertFrom-Json
if ($manifest.assets.Count -ne 20) { throw 'The first sample batch must export all 20 model variants.' }
Write-Output 'Exported editable source and 20 FBX models. Run DemoGenerate to import and regenerate the existing scene.'
