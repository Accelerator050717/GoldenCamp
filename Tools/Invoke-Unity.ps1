#requires -Version 7.0
<#
.SYNOPSIS
Runs a single, version-locked GoldCamp Unity task and verifies its result.
.DESCRIPTION
CreateProject requires -LicenseVerified after the operator has independently verified
the editor license. This switch is an explicit prerequisite, not a license check.
Tests omit -quit so Unity's asynchronous test runner can finish and exit itself.
All logs/results are unique files under Builds/Logs. No editor is downloaded here.
#>
[CmdletBinding(SupportsShouldProcess)]
param(
    [Parameter(Mandatory)]
    [ValidateSet('CreateProject', 'Compile', 'Generate', 'DemoGenerate', 'ArtPreview', 'EditModeTests', 'PlayModeTests', 'Build', 'DemoBuild', 'SmokeAutomation')]
    [string]$Action,
    [string]$EditorPath,
    [ValidateRange(30, 7200)]
    [int]$TimeoutSeconds = 1800,
    [switch]$WithGraphics,
    [switch]$LicenseVerified
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$pinnedVersion = '6000.3.23f1'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$projectPath = Join-Path $workspaceRoot 'GoldCamp.Unity'

if ([string]::IsNullOrWhiteSpace($EditorPath)) {
    if (-not [string]::IsNullOrWhiteSpace($env:GOLDCAMP_UNITY_EDITOR)) {
        $EditorPath = $env:GOLDCAMP_UNITY_EDITOR
    } else {
        $EditorPath = Join-Path $env:ProgramFiles "Unity\Hub\Editor\$pinnedVersion\Editor\Unity.exe"
    }
}
$EditorPath = [IO.Path]::GetFullPath($EditorPath)
if (-not (Test-Path -LiteralPath $EditorPath -PathType Leaf)) {
    throw "Editor missing: $EditorPath. Complete the existing Hub install, then pass -EditorPath."
}
$productVersion = (Get-Item -LiteralPath $EditorPath).VersionInfo.ProductVersion
$actualVersion = [regex]::Match([string]$productVersion, '^\d+\.\d+\.\d+[abfp]\d+').Value
if ($actualVersion -ne $pinnedVersion) {
    throw "Editor version '$productVersion' does not match required $pinnedVersion."
}

if ($Action -eq 'CreateProject') {
    if (-not $LicenseVerified) { throw 'CreateProject requires an independently verified license and -LicenseVerified.' }
    if (Test-Path -LiteralPath $projectPath) { throw "CreateProject refuses to overwrite an existing path: $projectPath" }
} else {
    $versionPath = Join-Path $projectPath 'ProjectSettings\ProjectVersion.txt'
    if (-not (Test-Path -LiteralPath $versionPath -PathType Leaf)) { throw "Unity project is missing: $versionPath" }
    $versionMatch = [regex]::Match((Get-Content -LiteralPath $versionPath -Raw), '(?m)^m_EditorVersion:\s*(\S+)\s*$')
    if (-not $versionMatch.Success -or $versionMatch.Groups[1].Value -ne $pinnedVersion) {
        throw "ProjectVersion.txt does not pin $pinnedVersion. No automatic upgrade is allowed."
    }
}

# Coordinate these runners, then check for an independently opened project editor.
$projectKey = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($projectPath.ToLowerInvariant())))
$taskMutex = [Threading.Mutex]::new($false, "Local\GoldCampUnity_$projectKey")
$ownsMutex = $false
$unityProcess = $null
try {
    try { $ownsMutex = $taskMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another GoldCamp Unity runner is active. Wait for that task; no duplicate editor was started.' }
    $existingEditors = @(Get-CimInstance Win32_Process -Filter "Name='Unity.exe'")
    foreach ($existing in $existingEditors) {
        $pathMatch = [regex]::Match([string]$existing.CommandLine, '(?i)(?:^|\s)-(?:projectPath|createProject)\s+(?:"(?<quoted>[^"]+)"|(?<plain>\S+))')
        if ($pathMatch.Success) {
            $openPath = if ($pathMatch.Groups['quoted'].Success) { $pathMatch.Groups['quoted'].Value } else { $pathMatch.Groups['plain'].Value }
            if ([IO.Path]::IsPathFullyQualified($openPath) -and [IO.Path]::GetFullPath($openPath).TrimEnd('\', '/') -ieq $projectPath.TrimEnd('\', '/')) {
                throw "GoldCamp is already open in Unity PID $($existing.ProcessId). Close that project editor before this task."
            }
        }
    }
    $lockPath = Join-Path $projectPath 'Temp\UnityLockfile'
    if (Test-Path -LiteralPath $lockPath -PathType Leaf) {
        try {
            $lockProbe = [IO.File]::Open($lockPath, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
            $lockProbe.Dispose()
        } catch [IO.IOException] { throw "The project lock is in use: $lockPath. No duplicate editor was started." }
    }
    if ($Action -eq 'CreateProject' -and (Test-Path -LiteralPath $projectPath)) { throw "Project path appeared during validation: $projectPath" }
    if (-not $PSCmdlet.ShouldProcess($projectPath, "$Action with Unity $pinnedVersion")) { return }

    $logsPath = Join-Path $workspaceRoot 'Builds\Logs'
    New-Item -ItemType Directory -Path $logsPath -Force | Out-Null
    $runId = '{0}-{1}-{2}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), $Action, ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $logPath = Join-Path $logsPath "$runId.log"
    $resultPath = Join-Path $logsPath "$runId.xml"
    $unityArguments = [Collections.Generic.List[string]]::new()
    $unityArguments.Add('-batchmode')
    if (-not $WithGraphics) { $unityArguments.Add('-nographics') }
    $unityArguments.Add('-logFile')
    $unityArguments.Add($logPath)
    if ($Action -eq 'CreateProject') {
        $unityArguments.Add('-createProject')
        $unityArguments.Add($projectPath)
    } else {
        $unityArguments.Add('-projectPath')
        $unityArguments.Add($projectPath)
    }
    $isTest = $Action -in @('EditModeTests', 'PlayModeTests')
    if ($isTest) {
        # A retained/open Demo scene can Awake when the PlayMode runner enters.
        # Keep any such profile access in a fresh marked root as well as player tests.
        $editorTestsParent = Join-Path $workspaceRoot '.local/EditorTests'
        foreach ($testParent in @((Join-Path $workspaceRoot '.local'), $editorTestsParent)) {
            if ((Test-Path -LiteralPath $testParent) -and ((Get-Item -LiteralPath $testParent).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
                throw 'Refusing a redirected Editor test root.'
            }
        }
        $editorTestRoot = Join-Path $editorTestsParent $runId
        if (Test-Path -LiteralPath $editorTestRoot) { throw 'Editor test root already exists; refusing reuse.' }
        [IO.Directory]::CreateDirectory($editorTestRoot) | Out-Null
        [IO.File]::WriteAllText((Join-Path $editorTestRoot '.goldcamp-test-root'), 'GoldCamp isolated Editor test profile.')
        $unityArguments.Add('--gc-test-root'); $unityArguments.Add($editorTestRoot)
        $unityArguments.Add('-runTests')
        $unityArguments.Add('-testPlatform')
        $unityArguments.Add($(if ($Action -eq 'EditModeTests') { 'EditMode' } else { 'PlayMode' }))
        $unityArguments.Add('-testResults')
        $unityArguments.Add($resultPath)
    } else {
        if ($Action -notin @('CreateProject', 'Compile')) {
            $method = switch ($Action) {
                'Generate' { 'GoldCamp.Editor.M0Setup.CreateScene' }
                'Build' { 'GoldCamp.Editor.M0Build.BuildWindows' }
                'DemoGenerate' { 'GoldCamp.Demo.Editor.DemoSetup.Generate' }
                'ArtPreview' { 'GoldCamp.Demo.Editor.CampArtPreview.Render' }
                'DemoBuild' { 'GoldCamp.Demo.Editor.DemoSetup.BuildWindows' }
                'SmokeAutomation' { 'GoldCamp.Editor.M0Automation.Run' }
            }
            $unityArguments.Add('-executeMethod')
            $unityArguments.Add($method)
        }
        $unityArguments.Add('-quit')
    }

    $startInfo = [Diagnostics.ProcessStartInfo]::new()
    $startInfo.FileName = $EditorPath
    $startInfo.WorkingDirectory = $workspaceRoot
    $startInfo.UseShellExecute = $false
    $startInfo.CreateNoWindow = $true
    $startInfo.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    foreach ($argument in $unityArguments) { $startInfo.ArgumentList.Add($argument) }
    $unityProcess = [Diagnostics.Process]::Start($startInfo)
    Write-Host "Started $Action, PID $($unityProcess.Id). Log: $logPath"
    if (-not $unityProcess.WaitForExit($TimeoutSeconds * 1000)) {
        if (-not $unityProcess.HasExited) { $unityProcess.Kill(); [void]$unityProcess.WaitForExit(10000) }
        throw "$Action exceeded $TimeoutSeconds seconds; only the editor started by this runner was stopped. See $logPath"
    }
    $exitCode = $unityProcess.ExitCode
    if ($exitCode -ne 0) { throw "Unity $Action exited with code $exitCode. See $logPath" }
    if (-not $isTest) {
        # Unity may return zero after an exception raised while adding a component.
        $unexpectedErrors = @(Select-String -LiteralPath $logPath -Pattern '^(UnityException|NullReferenceException|MissingReferenceException|InvalidOperationException|ArgumentException|TypeLoadException):|\berror CS\d{4}:')
        if ($unexpectedErrors.Count -gt 0) { throw "Unity $Action logged $($unexpectedErrors.Count) unexpected exceptions/compiler errors despite exit 0. See $logPath" }
    }

    $testSummary = $null
    if ($isTest) {
        if (-not (Test-Path -LiteralPath $resultPath -PathType Leaf)) { throw "Unity exited without a test results file: $resultPath" }
        [xml]$testXml = Get-Content -LiteralPath $resultPath -Raw
        $run = $testXml.DocumentElement
        if ($run.Name -ne 'test-run') { throw "Unexpected test results format in $resultPath" }
        $total = [int]::Parse($run.GetAttribute('total'))
        $passed = [int]::Parse($run.GetAttribute('passed'))
        $failed = [int]::Parse($run.GetAttribute('failed'))
        $result = $run.GetAttribute('result')
        if ($total -le 0 -or $passed -le 0 -or $failed -ne 0 -or $result -ne 'Passed') {
            throw "Tests did not pass: total=$total passed=$passed failed=$failed result=$result. See $resultPath"
        }
        $testSummary = [pscustomobject]@{ Total = $total; Passed = $passed; Failed = $failed; Result = $result; XmlPath = $resultPath }
    }
    if ($Action -eq 'CreateProject' -and -not (Test-Path -LiteralPath (Join-Path $projectPath 'ProjectSettings\ProjectVersion.txt'))) {
        throw "Editor exited without creating ProjectVersion.txt. See $logPath"
    }
    $buildPath = $null
    if ($Action -in @('Build', 'DemoBuild')) {
        $buildPath = Join-Path $workspaceRoot $(if ($Action -eq 'DemoBuild') { 'Builds\DemoPrototype\Windows\GoldCamp.exe' } else { 'Builds\M0\Windows\GoldCamp.exe' })
        if (-not (Test-Path -LiteralPath $buildPath -PathType Leaf)) { throw "Build did not produce $buildPath. See $logPath" }
        foreach ($requiredPath in @((Join-Path (Split-Path $buildPath -Parent) 'UnityPlayer.dll'), (Join-Path (Split-Path $buildPath -Parent) 'GoldCamp_Data'))) {
            if (-not (Test-Path -LiteralPath $requiredPath)) { throw "Build is missing required companion output: $requiredPath" }
        }
        if ($Action -eq 'DemoBuild') {
            $sourceFingerprint = & (Join-Path $PSScriptRoot 'Get-UnitySourceFingerprint.ps1')
            $builtCommit = (& git -C $workspaceRoot rev-parse HEAD).Trim()
            if ($LASTEXITCODE -ne 0) { throw 'Cannot record build source revision.' }
            [ordered]@{ BuiltUtc = [DateTime]::UtcNow.ToString('O'); Unity = $actualVersion; CommitAtBuild = $builtCommit
                Source = $sourceFingerprint; RuntimeFiles = @(& (Join-Path $PSScriptRoot 'Get-PlayerRuntimeFiles.ps1'))
                Log = [IO.Path]::GetFileName($logPath); Development = $true } |
                ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path (Split-Path $buildPath -Parent) 'build-info.json') -Encoding utf8NoBOM
        }
    }
    [pscustomobject]@{ Action = $Action; EditorVersion = $actualVersion; ExitCode = $exitCode; LogPath = $logPath; Tests = $testSummary; BuildPath = $buildPath }
} finally {
    if ($null -ne $unityProcess) { $unityProcess.Dispose() }
    if ($ownsMutex) { $taskMutex.ReleaseMutex() }
    $taskMutex.Dispose()
}
