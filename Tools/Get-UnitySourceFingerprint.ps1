#requires -Version 7.0
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
$campRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$paths = @(& git -C $campRoot -c core.quotepath=false ls-files --cached --others --exclude-standard -- 'GoldCamp.Unity/Assets' 'GoldCamp.Unity/Packages' 'GoldCamp.Unity/ProjectSettings')
if ($LASTEXITCODE -ne 0 -or $paths.Count -eq 0) { throw 'Could not enumerate Unity project sources.' }
$entries = [Collections.Generic.List[string]]::new()
foreach ($relative in ($paths | Sort-Object -Unique -CaseSensitive)) {
    $source = Join-Path $campRoot $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) { continue }
    if ($relative -eq 'GoldCamp.Unity/Assets/Resources/PerformanceTestRunInfo.json' -or
        $relative -eq 'GoldCamp.Unity/Assets/Resources/PerformanceTestRunInfo.json.meta') { continue }
    $hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
    $entries.Add($relative.Replace('\', '/') + "`0" + $hash)
}
$bytes = [Text.Encoding]::UTF8.GetBytes([string]::Join("`n", $entries))
[pscustomobject]@{ Algorithm = 'SHA256(sorted relative-path NUL content-sha256 LF)'; FileCount = $entries.Count
    Hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() }
