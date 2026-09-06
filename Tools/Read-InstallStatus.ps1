#requires -Version 7.0
<#
.SYNOPSIS
Prints one read-only installation snapshot. Does not launch, wait for or install Unity.
.DESCRIPTION
Process existence and files are observations, not proof of installation or licensing.
No command lines, credentials, license files or raw Hub logs are printed.
#>
[CmdletBinding()]
param([string]$EditorPath)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$pinnedVersion = '6000.3.23f1'
if ([string]::IsNullOrWhiteSpace($EditorPath)) {
    if (-not [string]::IsNullOrWhiteSpace($env:GOLDCAMP_UNITY_EDITOR)) {
        $EditorPath = $env:GOLDCAMP_UNITY_EDITOR
    } else {
        $EditorPath = Join-Path $env:ProgramFiles "Unity\Hub\Editor\$pinnedVersion\Editor\Unity.exe"
    }
}
$EditorPath = [IO.Path]::GetFullPath($EditorPath)
$editorExists = Test-Path -LiteralPath $EditorPath -PathType Leaf
$editorVersion = if ($editorExists) { (Get-Item -LiteralPath $EditorPath).VersionInfo.ProductVersion } else { $null }
$supportPath = Join-Path (Split-Path $EditorPath -Parent) 'Data\PlaybackEngines\WindowsStandaloneSupport'
$variationsPath = Join-Path $supportPath 'Variations'
$monoDirectories = @(if (Test-Path -LiteralPath $variationsPath -PathType Container) {
    Get-ChildItem -LiteralPath $variationsPath -Directory | Where-Object Name -Match '^win64.*mono$' | Select-Object -ExpandProperty Name
})
$hubCli = $null
$hubCliConfig = Join-Path $env:APPDATA 'UnityHub\cli-install.json'
if (Test-Path -LiteralPath $hubCliConfig -PathType Leaf) {
    try {
        $cli = Get-Content -LiteralPath $hubCliConfig -Raw | ConvertFrom-Json -AsHashtable
        $hubCli = [pscustomobject]@{ Path = $cli['path']; Version = $cli['version']; Exists = $(if ($cli['path']) { Test-Path -LiteralPath $cli['path'] -PathType Leaf } else { $false }) }
    } catch { Write-Warning 'Hub CLI registration could not be read; no changes made.' }
}
$processes = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe' OR Name='Unity Hub.exe' OR Name LIKE 'UnitySetup%'" | Select-Object ProcessId, Name, ExecutablePath)
$disks = @(Get-CimInstance Win32_LogicalDisk -Filter "DeviceID='C:' OR DeviceID='M:'" | Select-Object DeviceID, @{Name='FreeGiB';Expression={[math]::Round($_.FreeSpace / 1GB, 2)}})
[pscustomobject]@{
    CheckedAt = (Get-Date).ToString('o')
    PinnedEditorVersion = $pinnedVersion
    EditorPath = $EditorPath
    EditorFileExists = $editorExists
    EditorProductVersion = $editorVersion
    WindowsStandaloneSupportExists = (Test-Path -LiteralPath $supportPath -PathType Container)
    WindowsX64MonoPlayerDirectories = $monoDirectories
    HubCli = $hubCli
    RelatedProcesses = $processes
    Disks = $disks
    LicenseStatus = 'Not tested by this read-only snapshot.'
    InstallationStatus = 'Not inferred from process presence. Inspect the existing Hub task if needed.'
} | ConvertTo-Json -Depth 5
