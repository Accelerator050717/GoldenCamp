#requires -Version 7.4
<#
.SYNOPSIS
Starts or reuses GoldCamp's project-local MCP for Unity HTTP server.
.DESCRIPTION
Uses only .local/bridge-venv and loopback port 8080. Does not install packages,
configure clients, launch Unity, or stop an existing server. A listening port is
not evidence that Unity or any MCP client has connected.
#>
[CmdletBinding(SupportsShouldProcess)]
param([ValidateNotNullOrEmpty()][string]$UnityInstance = 'GoldCamp.Unity')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$venvPath = Join-Path $workspaceRoot '.local\bridge-venv'
$pythonPath = Join-Path $venvPath 'Scripts\python.exe'
$serverPath = Join-Path $venvPath 'Scripts\mcp-for-unity.exe'
$serverVersion = '10.2.0'
$httpUrl = 'http://127.0.0.1:8080'
$mcpUrl = "$httpUrl/mcp"
$pidPath = Join-Path $workspaceRoot '.local\unity-bridge-process.json'

foreach ($requiredPath in @($pythonPath, $serverPath)) {
    if (-not (Test-Path -LiteralPath $requiredPath -PathType Leaf)) {
        throw "Project-local bridge dependency is missing: $requiredPath. Finish the pinned environment setup first."
    }
}
$installedVersion = & $pythonPath -c 'import importlib.metadata; print(importlib.metadata.version("mcpforunityserver"))'
if ($LASTEXITCODE -ne 0 -or ([string]$installedVersion).Trim() -ne $serverVersion) {
    throw "Expected mcpforunityserver $serverVersion in $venvPath; found '$installedVersion'."
}

$taskMutex = [Threading.Mutex]::new($false, 'Local\GoldCampUnityBridge8080')
$ownsMutex = $false
try {
    try { $ownsMutex = $taskMutex.WaitOne(0) } catch [Threading.AbandonedMutexException] { $ownsMutex = $true }
    if (-not $ownsMutex) { throw 'Another GoldCamp bridge start is in progress; no second server was started.' }

    $candidateProcesses = @(Get-CimInstance Win32_Process -Filter "Name='python.exe' OR Name='mcp-for-unity.exe' OR Name='uv.exe' OR Name='uvx.exe'")
    $bridgeProcesses = @($candidateProcesses | Where-Object { [string]$_.CommandLine -match '(?i)mcp-for-unity(?:\.exe)?(?:["\s]|$)' })
    $localProcesses = @($bridgeProcesses | Where-Object { [string]$_.CommandLine -match [regex]::Escape($serverPath) })
    $otherProcesses = @($bridgeProcesses | Where-Object { [string]$_.CommandLine -notmatch [regex]::Escape($serverPath) })
    if ($otherProcesses.Count -gt 0) {
        throw "An MCP for Unity process from a different environment already exists (PID $($otherProcesses.ProcessId -join ', ')). No second bridge was started."
    }
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction SilentlyContinue | Where-Object LocalPort -EQ 8080)
    if ($localProcesses.Count -gt 0) {
        foreach ($existing in $localProcesses) {
            if ([string]$existing.CommandLine -notmatch [regex]::Escape($httpUrl)) {
                throw "The project bridge is already running with different arguments (PID $($existing.ProcessId)). No changes made."
            }
            if ([string]$existing.CommandLine -notmatch ('--default-instance\s+"?' + [regex]::Escape($UnityInstance) + '(?:"|\s|$)')) {
                throw "The project bridge is already running with a different Unity instance selection (PID $($existing.ProcessId)). No changes made."
            }
        }
        $localPids = @($localProcesses.ProcessId)
        if (@($listeners | Where-Object { $_.OwningProcess -notin $localPids }).Count -gt 0) {
            throw 'Port 8080 is owned by another process. No changes made.'
        }
        [pscustomobject]@{
            Status = $(if ($listeners.Count -gt 0) { 'AlreadyListening' } else { 'AlreadyStarted; no listener observed yet' })
            ProcessIds = $localPids
            ServerVersion = $installedVersion
            McpUrl = $mcpUrl
            DefaultUnityInstance = $UnityInstance
            UnityConnection = 'Not verified by this helper'
        }
        return
    }
    if ($listeners.Count -gt 0) { throw "Port 8080 is already occupied (PID $($listeners.OwningProcess -join ', ')). No server was started." }
    if (-not $PSCmdlet.ShouldProcess($httpUrl, "Start project-local mcpforunityserver $serverVersion")) { return }

    $logsPath = Join-Path $workspaceRoot 'Builds\Logs'
    New-Item -ItemType Directory -Path $logsPath -Force | Out-Null
    $runId = 'unity-bridge-{0}-{1}' -f (Get-Date -Format 'yyyyMMdd-HHmmss'), ([guid]::NewGuid().ToString('N').Substring(0, 8))
    $stdoutPath = Join-Path $logsPath "$runId.stdout.log"
    $stderrPath = Join-Path $logsPath "$runId.stderr.log"
    if ($UnityInstance -match '["\r\n]') { throw 'UnityInstance must not contain quotes or line breaks.' }
    $childEnvironment = @{
        UNITY_MCP_LOG_DIR = $logsPath
        UNITY_MCP_SKIP_STARTUP_CONNECT = '1'
        UNITY_MCP_HTTP_URL = $httpUrl
        UNITY_MCP_HTTP_HOST = '127.0.0.1'
        UNITY_MCP_HTTP_PORT = '8080'
        UNITY_MCP_HTTP_REMOTE_HOSTED = 'false'
    }
    $serverArguments = @('--transport', 'http', '--http-url', $httpUrl, '--http-host', '127.0.0.1', '--http-port', '8080', '--project-scoped-tools', '--default-instance', ('"' + $UnityInstance + '"'))
    $serverProcess = Start-Process -FilePath $serverPath -ArgumentList $serverArguments -WorkingDirectory $workspaceRoot -Environment $childEnvironment -WindowStyle Hidden -RedirectStandardOutput $stdoutPath -RedirectStandardError $stderrPath -PassThru
    if ($serverProcess.WaitForExit(1500)) { throw "Bridge exited during startup with code $($serverProcess.ExitCode). See $stderrPath" }
    $record = [ordered]@{
        ProcessId = $serverProcess.Id
        StartedAtUtc = $serverProcess.StartTime.ToUniversalTime().ToString('o')
        ServerPath = $serverPath
        ServerVersion = $serverVersion
        HttpUrl = $httpUrl
        McpUrl = $mcpUrl
        DefaultUnityInstance = $UnityInstance
        StdoutPath = $stdoutPath
        StderrPath = $stderrPath
    }
    $record | ConvertTo-Json | Set-Content -LiteralPath $pidPath -Encoding utf8
    [pscustomobject]@{
        Status = 'Started; listening and Unity connection still require verification'
        ProcessId = $serverProcess.Id
        ServerVersion = $serverVersion
        McpUrl = $mcpUrl
        DefaultUnityInstance = $UnityInstance
        StdoutPath = $stdoutPath
        StderrPath = $stderrPath
        ProcessRecordPath = $pidPath
    }
} finally {
    if ($ownsMutex) { $taskMutex.ReleaseMutex() }
    $taskMutex.Dispose()
}
