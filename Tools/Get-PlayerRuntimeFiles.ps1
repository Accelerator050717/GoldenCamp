#requires -Version 7.0
# Exact Windows runtime files, excluding debug source backups and local profiles.
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$campRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = Join-Path $campRoot 'Builds/DemoPrototype/Windows'
$roots = @('GoldCamp.exe', 'UnityPlayer.dll', 'GoldCamp_Data', 'MonoBleedingEdge', 'Licenses',
    'UnityCrashHandler64.exe', 'D3D12', 'WinPixEventRuntime.dll')
if ((Get-Item -LiteralPath $buildRoot).Attributes -band [IO.FileAttributes]::ReparsePoint) {
    throw 'Refusing a redirected player directory.'
}
$files = foreach ($entry in $roots) {
    $path = Join-Path $buildRoot $entry
    if (-not (Test-Path -LiteralPath $path)) { continue }
    $item = Get-Item -LiteralPath $path
    $items = @($item)
    if ($item.PSIsContainer) { $items += @(Get-ChildItem -LiteralPath $path -Recurse -Force) }
    if (@($items | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) {
        throw "Refusing redirected runtime content: $entry"
    }
    $items | Where-Object { -not $_.PSIsContainer }
}
foreach ($file in ($files | Sort-Object FullName -Unique)) {
    [pscustomobject][ordered]@{
        Path = [IO.Path]::GetRelativePath($buildRoot, $file.FullName).Replace('\', '/')
        Bytes = $file.Length
        Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
