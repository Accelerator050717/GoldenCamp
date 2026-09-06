#requires -Version 7.0
<# Creates a new immutable local ZIP candidate. Does not upload or publish it. #>
[CmdletBinding()]
param()
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$campRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildRoot = Join-Path $campRoot 'Builds/DemoPrototype/Windows'
$stampPath = Join-Path $buildRoot 'build-info.json'
if (-not (Test-Path -LiteralPath $stampPath)) { throw 'Run the current DemoBuild wrapper first; source/build provenance is missing.' }
$stamp = Get-Content -LiteralPath $stampPath -Raw | ConvertFrom-Json
$current = & (Join-Path $PSScriptRoot 'Get-UnitySourceFingerprint.ps1')
if ($stamp.Source.Hash -ne $current.Hash) { throw 'Unity project sources changed after this build. Validate and rebuild before packaging.' }
$runtime = @(& (Join-Path $PSScriptRoot 'Get-PlayerRuntimeFiles.ps1'))
if (-not $stamp.PSObject.Properties['RuntimeFiles'] -or @($stamp.RuntimeFiles).Count -eq 0) {
    throw 'Build output hashes are missing. Run DemoBuild before packaging.'
}
$expectedFiles = @($stamp.RuntimeFiles | ForEach-Object { $_.Path + '|' + $_.Bytes + '|' + $_.Sha256 })
$actualFiles = @($runtime | ForEach-Object { $_.Path + '|' + $_.Bytes + '|' + $_.Sha256 })
if (@(Compare-Object -ReferenceObject $expectedFiles -DifferenceObject $actualFiles -CaseSensitive).Count) {
    throw 'Player runtime files changed after this build. Rebuild before packaging.'
}
foreach ($required in @('GoldCamp.exe', 'UnityPlayer.dll', 'GoldCamp_Data', 'MonoBleedingEdge', 'Licenses')) {
    if (-not (Test-Path -LiteralPath (Join-Path $buildRoot $required))) { throw "Missing build companion: $required" }
}
$guide = Join-Path $campRoot 'docs/FRIEND_PLAYTEST.md'
if (-not (Test-Path -LiteralPath $guide)) { throw 'Friend playtest instructions are missing.' }
$sha = (& git -C $campRoot rev-parse --short=12 HEAD).Trim()
if ($LASTEXITCODE -ne 0) { throw 'Cannot identify the source commit.' }
$candidateParent = Join-Path $campRoot 'Builds/Candidates'
if ((Test-Path -LiteralPath $candidateParent) -and ((Get-Item -LiteralPath $candidateParent).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
    throw 'Refusing a redirected candidate output directory.'
}
$name = 'GoldCamp-v0.3-' + (Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + $sha
$candidate = Join-Path $candidateParent $name
if ((Test-Path -LiteralPath $candidate) -or (Test-Path -LiteralPath ($candidate + '.zip'))) { throw 'Candidate already exists; refusing overwrite.' }
[IO.Directory]::CreateDirectory($candidate) | Out-Null
$windows = Join-Path $candidate 'Windows'
[IO.Directory]::CreateDirectory($windows) | Out-Null
# Exact distribution roots; never include local profiles, test roots or Unity source backups.
foreach ($entry in @('GoldCamp.exe', 'UnityPlayer.dll', 'GoldCamp_Data', 'MonoBleedingEdge', 'Licenses', 'build-info.json')) {
    Copy-Item -LiteralPath (Join-Path $buildRoot $entry) -Destination $windows -Recurse
}
foreach ($optional in @('UnityCrashHandler64.exe', 'D3D12', 'WinPixEventRuntime.dll')) {
    if (Test-Path -LiteralPath (Join-Path $buildRoot $optional)) { Copy-Item -LiteralPath (Join-Path $buildRoot $optional) -Destination $windows -Recurse }
}
Copy-Item -LiteralPath $guide -Destination (Join-Path $candidate '先读我-朋友试玩.md')
$files = @(Get-ChildItem -LiteralPath $candidate -File -Recurse | Sort-Object FullName | ForEach-Object {
    [ordered]@{ Path = [IO.Path]::GetRelativePath($candidate, $_.FullName).Replace('\', '/'); Bytes = $_.Length
        Sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
$manifest = [ordered]@{ CreatedUtc = [DateTime]::UtcNow.ToString('O'); CommitAtPackaging = $sha
    Source = $current; Build = $stamp; Files = $files; Published = $false }
$manifest | ConvertTo-Json -Depth 15 | Set-Content -LiteralPath (Join-Path $candidate 'manifest.json') -Encoding utf8NoBOM
Compress-Archive -LiteralPath $candidate -DestinationPath ($candidate + '.zip') -CompressionLevel Optimal
$zipHash = (Get-FileHash -LiteralPath ($candidate + '.zip') -Algorithm SHA256).Hash.ToLowerInvariant()
($zipHash + '  ' + $name + '.zip') | Set-Content -LiteralPath ($candidate + '.zip.sha256') -Encoding ascii
[pscustomobject]@{ Directory = $candidate; Zip = $candidate + '.zip'; Sha256 = $zipHash; Files = $files.Count; Published = $false }
