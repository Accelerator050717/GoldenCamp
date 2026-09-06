#requires -Version 7.0
<#
.SYNOPSIS
Exercises checkpoint F using four real local FishNet player processes and networked movement axes.
.DESCRIPTION
Creates fresh, marked test roots under .local/TestRuns; never accepts a save path.
CampTestDriver consumes inbox.jsonl. commands.jsonl is an identical audit copy.
Exercises late joins, four-way contention, private accounts, per-player movement, reconnect and save reopening.
Uses only the Development A fixture a-ore; it does not prove normal resource progression.
This is four local processes, not four humans, LAN, Steam, voice, rendering performance or FPS validation.
-IncludeHostCrash adds an explicit host-only termination after confirmed transactions; default exits are graceful.
Use -ValidateInputMathOnly to check movement calculations without files or player processes.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 300)] [int]$TimeoutSeconds = 90,
    [switch]$WithGraphics,
    [switch]$IncludeHostCrash,
    [switch]$ValidateInputMathOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-WalkAxis([double]$Distance) {
    # Double literals select Math.Min/Max(double,double), preserving small analog axes.
    return [Math]::Min(0.7, [Math]::Max(0.04, $Distance / 3.2))
}

function Get-ClimbAxis([double]$Delta) {
    return [Math]::Sign($Delta) * [Math]::Min(1.0, [Math]::Max(0.16, [Math]::Abs($Delta)))
}

function Test-InputMath {
    $cases = @(
        @{ Name = 'walk .4m remains fractional'; Actual = (Get-WalkAxis .4); Expected = .125 },
        @{ Name = 'walk .1m stays nonzero'; Actual = (Get-WalkAxis .1); Expected = .04 },
        @{ Name = 'walk .8m slows before arrival'; Actual = (Get-WalkAxis .8); Expected = .25 },
        @{ Name = 'walk maximum is conservative'; Actual = (Get-WalkAxis 10); Expected = .7 },
        @{ Name = 'climb near target upward'; Actual = (Get-ClimbAxis .04); Expected = .16 },
        @{ Name = 'climb near target downward'; Actual = (Get-ClimbAxis -.04); Expected = -.16 },
        @{ Name = 'climb upward maximum'; Actual = (Get-ClimbAxis 2); Expected = 1.0 },
        @{ Name = 'climb downward maximum'; Actual = (Get-ClimbAxis -2); Expected = -1.0 },
        @{ Name = 'climb no delta'; Actual = (Get-ClimbAxis 0); Expected = 0.0 }
    )
    foreach ($case in $cases) {
        if ($case.Actual -isnot [double] -or [Math]::Abs($case.Actual - $case.Expected) -gt .0000001) {
            throw "Input math regression: $($case.Name); expected $($case.Expected), got $($case.Actual)."
        }
    }
    # Behavioral checks across distances catch integer truncation and abrupt speed jumps.
    $previous = 0.0
    foreach ($distance in @(0.001, 0.04, 0.1, 0.4, 0.8, 1.6, 3.2, 10.0)) {
        $axis = Get-WalkAxis $distance
        if ($axis -isnot [double] -or $axis -le 0.0 -or $axis -gt .7 -or $axis -lt $previous) {
            throw "Walk input must remain fractional, positive, bounded, and monotonic; distance=$distance, axis=$axis."
        }
        $previous = $axis
    }
    return [pscustomobject]@{ Success = $true; PureMathChecks = ($cases.Count + 8); GameOrUnityStarted = $false }
}

if ($ValidateInputMathOnly) { Test-InputMath; return }

$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildPath = Join-Path $workspaceRoot 'Builds\DemoPrototype\Windows\GoldCamp.exe'
if (-not (Test-Path -LiteralPath $buildPath -PathType Leaf)) {
    throw "Build the Development player first: $buildPath"
}
$gameAssemblyPath = Join-Path (Split-Path -Parent $buildPath) 'GoldCamp_Data\Managed\GoldCamp.Demo.dll'
if (-not (Test-Path -LiteralPath $gameAssemblyPath -PathType Leaf)) {
    throw "The Development player's gameplay assembly is missing: $gameAssemblyPath"
}
$runsParent = Join-Path $workspaceRoot '.local\TestRuns'
foreach ($directory in @((Join-Path $workspaceRoot '.local'), $runsParent)) {
    if ((Test-Path -LiteralPath $directory) -and
        ((Get-Item -LiteralPath $directory).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "Refusing a redirected test directory: $directory"
    }
}
$runRoot = Join-Path $runsParent ((Get-Date).ToUniversalTime().ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N'))
if (Test-Path -LiteralPath $runRoot) { throw 'The generated test directory already exists.' }
[IO.Directory]::CreateDirectory($runRoot) | Out-Null
$utf8 = [Text.UTF8Encoding]::new($false, $true)
$suiteTimer = [Diagnostics.Stopwatch]::StartNew()
$processes = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
$roles = @{}
$evidence = [ordered]@{
    SchemaVersion = 1
    Suite = 'F-four-local-processes-isolated-loopback'
    Mode = $(if ($WithGraphics) { 'Automated player processes with graphics' } else { 'Automated -batchmode -nographics player processes' })
    NotValidated = @('four humans', 'human input', 'visual readability', 'FPS', 'normal resource progression', 'LAN devices', 'cross-network play', 'Steam transport', 'voice')
    StartedUtc = [DateTime]::UtcNow.ToString('O')
    FinishedUtc = $null
    Success = $false
    RunRoot = $runRoot
    Build = [ordered]@{
        Path = $buildPath
        Sha256 = (Get-FileHash -LiteralPath $buildPath -Algorithm SHA256).Hash.ToLowerInvariant()
        LastWriteUtc = (Get-Item -LiteralPath $buildPath).LastWriteTimeUtc.ToString('O')
        GameAssembly = [ordered]@{
            Path = $gameAssemblyPath
            Sha256 = (Get-FileHash -LiteralPath $gameAssemblyPath -Algorithm SHA256).Hash.ToLowerInvariant()
            LastWriteUtc = (Get-Item -LiteralPath $gameAssemblyPath).LastWriteTimeUtc.ToString('O')
        }
    }
    Port = 0
    Scenario = [ordered]@{}
    Checks = $checks
    Processes = @()
    Error = $null
}

function Assert-TestPath([string]$Path) {
    $fullPath = [IO.Path]::GetFullPath($Path)
    if (-not $fullPath.StartsWith($runRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Path is outside this test run: $fullPath"
    }
    return $fullPath
}

function Assert-Check([bool]$Condition, [string]$Name, [string]$Detail = '') {
    $checks.Add([ordered]@{ Name = $Name; Passed = $Condition; Detail = $Detail })
    if (-not $Condition) { throw "Assertion failed: $Name. $Detail" }
}

function Write-AtomicText([string]$Path, [string]$Text) {
    $Path = Assert-TestPath $Path
    $temporary = $Path + '.tmp-' + [Guid]::NewGuid().ToString('N')
    try {
        [IO.File]::WriteAllText($temporary, $Text, $utf8)
        for ($attempt = 0; $attempt -lt 30; $attempt++) {
            try {
                [IO.File]::Move($temporary, $Path, $true)
                return
            } catch [IO.IOException], [UnauthorizedAccessException] {
                # Windows overwrite can report AccessDenied while a reader holds the target.
                # Keep the same bounded retry; permanent access errors still rethrow unchanged.
                if ($attempt -eq 29) { throw }
                Start-Sleep -Milliseconds 50
            }
        }
    } finally {
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Read-JsonSnapshot([string]$Path) {
    try {
        $text = [IO.File]::ReadAllText((Assert-TestPath $Path), $utf8)
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        return @{ Data = (ConvertFrom-Json -InputObject $text -AsHashtable -Depth 100); Raw = $text }
    } catch {
        # view.json is rewritten by the player. Missing/locked/partial JSON is retried.
        return $null
    }
}

function Read-Results([string]$Root) {
    try { $text = [IO.File]::ReadAllText((Assert-TestPath (Join-Path $Root 'results.jsonl')), $utf8) }
    catch { return }
    $lines = $text -split "`n"
    # A writer may still be appending the last line. Only consume newline-terminated records.
    for ($index = 0; $index -lt $lines.Length - 1; $index++) {
        if ([string]::IsNullOrWhiteSpace($lines[$index])) { continue }
        try { ConvertFrom-Json -InputObject $lines[$index] -AsHashtable -Depth 30 }
        catch { return }
    }
}

function Assert-ExpectedProcessesAlive {
    if ($suiteTimer.Elapsed.TotalMinutes -gt 18) { throw 'Four-peer suite exceeded its 18-minute hard deadline.' }
    foreach ($entry in $processes) {
        if ($entry.ExpectedRunning -and $entry.Process.HasExited) {
            throw "Player $($entry.Role)/$($entry.Phase) PID $($entry.Id) exited unexpectedly. Log: $($entry.LogPath)"
        }
    }
}

function Wait-Until([string]$Description, [scriptblock]$Probe) {
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt $TimeoutSeconds) {
        Assert-ExpectedProcessesAlive
        $answer = & $Probe
        if ($null -ne $answer -and $answer -ne $false) { return $answer }
        Start-Sleep -Milliseconds 150
    }
    throw "Timed out waiting for $Description. Inspect this run's player logs: $runRoot"
}

function Wait-View([string]$ViewRole, [scriptblock]$Condition) {
    # PowerShell callbacks inherit dynamic scope: do not shadow the caller's $role,
    # which may be the OTHER player whose raw-stone privacy is being inspected.
    $viewRoot = $roles[$ViewRole].Root
    return Wait-Until "$ViewRole client projection" {
        $snapshot = Read-JsonSnapshot (Join-Path $viewRoot 'view.json')
        if ($null -eq $snapshot -or $null -eq $snapshot.Data['Self']) { return $null }
        if (& $Condition $snapshot.Data) { return $snapshot }
        return $null
    }
}

function Wait-Result([string]$Role, [string]$RequestId, [int]$Count = 1) {
    $root = $roles[$Role].Root
    return Wait-Until "$Role result $RequestId number $Count" {
        $matches = @(Read-Results $root | Where-Object { $_['RequestId'] -eq $RequestId })
        if ($matches.Count -ge $Count) { return @{ Entries = $matches } }
        return $null
    }
}

function New-Command([string]$RequestId, [string]$Action, [string]$TargetId = '',
    [int]$ExpectedVersion = 0, [string]$OtherPlayerId = '', [long]$Amount = 0) {
    return [ordered]@{
        RequestId = $RequestId; Action = $Action; TargetId = $TargetId
        OtherPlayerId = $OtherPlayerId; ExpectedVersion = $ExpectedVersion; Amount = $Amount
        Direction = [ordered]@{ x = 0; y = 0; z = 0 }
    }
}

function Send-Command([string]$Role, $Command) {
    $entry = $roles[$Role]
    $line = ConvertTo-Json -InputObject $Command -Compress -Depth 10
    $allLines = @($entry.InboxLines.ToArray()) + $line
    $text = ($allLines -join "`n") + "`n"
    # Replace the complete inbox, never expose a half-written command to the driver.
    Write-AtomicText (Join-Path $entry.Root 'inbox.jsonl') $text
    $entry.InboxLines.Add($line)
    Write-AtomicText (Join-Path $entry.Root 'commands.jsonl') $text
}

function Start-Player([string]$Role, [string]$Phase, [bool]$NewWorld) {
    $root = $roles[$Role].Root
    $logPath = Assert-TestPath (Join-Path $root ("player-$Phase.log"))
    $start = [Diagnostics.ProcessStartInfo]::new()
    $start.FileName = $buildPath
    $start.WorkingDirectory = Split-Path -Parent $buildPath
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
    $arguments = [Collections.Generic.List[string]]::new()
    if (-not $WithGraphics) { $arguments.Add('-batchmode'); $arguments.Add('-nographics') }
    foreach ($argument in @('-logFile', $logPath, '--gc-test-root', $root, '--gc-port', [string]$evidence.Port)) {
        $arguments.Add($argument)
    }
    if ($Role -eq 'host') {
        $arguments.Add('--gc-host'); $arguments.Add('--gc-test-fixture')
        if ($NewWorld) { $arguments.Add('--gc-new'); $arguments.Add('--gc-seed'); $arguments.Add([string]$fourPeerSeed) }
    } else {
        $arguments.Add('--gc-connect'); $arguments.Add('127.0.0.1')
    }
    $arguments.Add('--gc-auto-input')
    Set-Input $Role 0 0 0 0 $false 'launch-neutral'
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $playerProcess = [Diagnostics.Process]::Start($start)
    if ($null -eq $playerProcess) { throw "Could not start $Role player." }
    $entry = [pscustomobject]@{
        Role = $Role; Phase = $Phase; Root = $root; Process = $playerProcess
        Id = $playerProcess.Id; StartedUtc = [DateTime]::UtcNow.ToString('O')
        LogPath = $logPath; ExpectedRunning = $true; Forced = $false; IntentionalCrash = $false; MaxObservedRss = $null
    }
    $processes.Add($entry)
    $roles[$Role].CurrentProcess = $entry
    $null = Wait-Until "$Role test driver PID" {
        $snapshot = Read-JsonSnapshot (Join-Path $root 'process.json')
        if ($null -ne $snapshot -and $snapshot.Data['pid'] -eq $entry.Id) { return $true }
        return $null
    }
}

function Stop-Player([string]$Role) {
    $entry = $roles[$Role].CurrentProcess
    Set-Neutral $Role 'shutdown'
    Send-Command $Role (New-Command "f-shutdown-$Role-$($entry.Phase)" 'shutdown')
    $entry.ExpectedRunning = $false
    $null = Wait-Until "$Role/$($entry.Phase) normal exit" {
        if ($entry.Process.HasExited) { return $true }
        return $null
    }
    $entry.Process.WaitForExit()
    Assert-Check ($entry.Process.ExitCode -eq 0) "$Role/$($entry.Phase) exited normally" "PID $($entry.Id); exit $($entry.Process.ExitCode)"
}

function Archive-ControlFiles([string]$Role, [string]$Prefix = 'first') {
    $entry = $roles[$Role]
    foreach ($file in @('view.json', 'results.jsonl', 'inbox.jsonl', 'commands.jsonl', 'process.json', 'input.json', 'inputs.jsonl')) {
        $source = Assert-TestPath (Join-Path $entry.Root $file)
        $destination = Assert-TestPath (Join-Path $entry.Root ("$Prefix-$file"))
        if (Test-Path -LiteralPath $source) { Move-Item -LiteralPath $source -Destination $destination }
    }
    $entry.InboxLines.Clear()
}

function Test-ProjectionFields($Node, [string]$Path = '$') {
    if ($Node -is [Collections.IDictionary]) {
        foreach ($key in $Node.Keys) {
            if ($key -in @('CredentialHash', 'HiddenValue', 'CoreKind', 'InspectedBy', 'RawClueBand', 'RawWindowBand', 'Secret')) { return $false }
            $childPath = "$Path.$key"
            if ($key -eq 'Balance' -and $childPath -ne '$.Self.Balance') { return $false }
            if (-not (Test-ProjectionFields $Node[$key] $childPath)) { return $false }
        }
    } elseif ($Node -is [Collections.IEnumerable] -and $Node -isnot [string]) {
        foreach ($child in $Node) {
            if (-not (Test-ProjectionFields $child "$Path[]")) { return $false }
        }
    }
    return $true
}

function Assert-Projection($Snapshot, [string]$PlayerId, [string]$Label) {
    $valid = $Snapshot.Data['Self']['Id'] -eq $PlayerId -and
        $Snapshot.Data['Self'].Contains('Balance') -and
        (Test-ProjectionFields $Snapshot.Data) -and
        $Snapshot.Raw -notmatch 'CredentialHash|HiddenValue|host-secret-test'
    Assert-Check $valid "$Label projection privacy" 'Only Self may contain Balance; no host-only fields or secrets.'
    Write-AtomicText (Join-Path $runRoot ("view-$Label.json")) $Snapshot.Raw
}

function Assert-ResultSame($Actual, $Expected, [string]$Label) {
    $same = $true
    foreach ($key in @('RequestId', 'Success', 'Code', 'Message', 'Amount', 'Revision')) {
        if ($Actual[$key] -cne $Expected[$key]) { $same = $false }
    }
    Assert-Check $same $Label 'Original request returns its original durable result.'
}

function Read-World([string]$WorldId) {
    if ($WorldId -notmatch '^[0-9a-f]{32}$') { throw 'Invalid world ID in test projection.' }
    $path = Assert-TestPath (Join-Path $roles['host'].Root "Camps\$WorldId\world.camp.json")
    $snapshot = Read-JsonSnapshot $path
    if ($null -eq $snapshot) { throw 'The stopped host did not leave readable committed world JSON.' }
    $envelope = $snapshot.Data
    if ($envelope['FormatVersion'] -ne 1 -or $envelope['SchemaVersion'] -ne 1 -or $envelope['Payload'] -isnot [string]) {
        throw 'Unexpected committed save envelope.'
    }
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($utf8.GetBytes($envelope['Payload']))).ToLowerInvariant()
    Assert-Check ($digest -ceq $envelope['Sha256']) 'Committed save checksum matches'
    try { $state = ConvertFrom-Json -InputObject $envelope['Payload'] -AsHashtable -Depth 100 }
    catch { throw 'Committed save payload is invalid JSON; content withheld.' }
    return $state
}


$fourPeerSeed = 6
$commandNumber = 0
$evidence.Scenario['Seed'] = $fourPeerSeed
$evidence.Scenario['InputProtocol'] = 'input.json -> CampTestDriver -> FishNet SendInput; inputs.jsonl records frames'
$evidence.Scenario['Movement'] = [Collections.Generic.List[object]]::new()

function Find-Entity($View, [string]$Collection, [string]$Id) {
    $found = @($View[$Collection] | Where-Object { $_['Id'] -ceq $Id })
    if ($found.Count -ne 1) { throw "Expected exactly one $Collection entry $Id; observed $($found.Count)." }
    return $found[0]
}
function Get-LiveView([string]$Role) {
    return Wait-View $Role { param($view) $null -ne $view['Rules'] -and
        @($view['Players'] | Where-Object { $_['Id'] -eq $view['Self']['Id'] -and $_['Online'] }).Count -eq 1 }
}
function Get-Miner($View) { return Find-Entity $View 'Players' $View['Self']['Id'] }

function Set-Input([string]$Role, [double]$X, [double]$Z, [double]$Yaw,
    [double]$Pitch = 0, [bool]$Jump = $false, [string]$Label = '', $Position = $null) {
    $entry = $roles[$Role]
    $frame = [ordered]@{ X = $X; Z = $Z; Yaw = $Yaw; Pitch = $Pitch; Jump = $Jump }
    Write-AtomicText (Join-Path $entry.Root 'input.json') (ConvertTo-Json $frame -Compress)
    $entry.LastYaw = $Yaw
    $audit = [ordered]@{ Utc = [DateTime]::UtcNow.ToString('O'); Label = $Label; Frame = $frame; ObservedPosition = $Position }
    [IO.File]::AppendAllText((Assert-TestPath (Join-Path $entry.Root 'inputs.jsonl')),
        (ConvertTo-Json $audit -Depth 10 -Compress) + [Environment]::NewLine, $utf8)
}
function Set-Neutral([string]$Role, [string]$Label = 'neutral') {
    Set-Input $Role 0 0 $roles[$Role].LastYaw 0 $false $Label
}
function Wait-GameSeconds([double]$Seconds) {
    $startTime = [double](Get-LiveView 'host').Data['OnlineSeconds']
    $null = Wait-View 'host' { param($view) [double]$view['OnlineSeconds'] -ge $startTime + $Seconds }
}
function Aim-At($Position, [double]$X, [double]$Y, [double]$Z) {
    $dx = $X - [double]$Position['x']; $dy = $Y - ([double]$Position['y'] + 1.6); $dz = $Z - [double]$Position['z']
    $length = [Math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
    if ($length -lt .001) { throw 'Cannot aim at the eye origin.' }
    return [ordered]@{ x = $dx / $length; y = $dy / $length; z = $dz / $length }
}
function New-Action([string]$Label, [string]$Action, [string]$Target = '',
    [int]$Version = 0, $Direction = $null) {
    $script:commandNumber++
    $command = New-Command ("f-$Label-" + $script:commandNumber) $Action $Target $Version
    if ($null -ne $Direction) { $command.Direction = $Direction }
    return $command
}
function Invoke-Action([string]$Role, $Command, [bool]$Success = $true) {
    Send-Command $Role $Command
    $result = (Wait-Result $Role $Command.RequestId).Entries[0]
    Assert-Check ($result['Success'] -eq $Success) "$Role $($Command.Action) $($Command.RequestId)" ("code=" + $result['Code'] + "; " + $result['Message'])
    $null = Wait-View $Role { param($view) [long]$view['Revision'] -ge [long]$result['Revision'] }
    return $result
}

function Move-To([string]$Role, [double]$X, [double]$Z, [string]$Label,
    [double]$FloorY = 0, [double]$HeightTolerance = .65) {
    $started = (Get-LiveView $Role).Data
    $from = (Get-Miner $started)['Position']
    $arrival = @{ Active = $false; StartTime = 0.0; LastTime = 0.0; LastX = 0.0; LastZ = 0.0; StableFrames = 0 }
    try {
        $null = Wait-Until "$Role walking $Label to ($X,$FloorY,$Z); no teleport fallback" {
            $snapshot = Read-JsonSnapshot (Join-Path $roles[$Role].Root 'view.json')
            if ($null -eq $snapshot -or $null -eq $snapshot.Data['Self']) { return $null }
            $position = (Get-Miner $snapshot.Data)['Position']
            if ([Math]::Abs([double]$position['y'] - $FloorY) -gt $HeightTolerance) {
                throw "$Role left the planned floor while walking $($Label): $($position | ConvertTo-Json -Compress)"
            }
            $dx = $X - [double]$position['x']; $dz = $Z - [double]$position['z']
            $distance = [Math]::Sqrt($dx * $dx + $dz * $dz)
            $gameTime = [double]$snapshot.Data['OnlineSeconds']
            if ($arrival.Active) {
                # Re-reading the same 5Hz projection is not evidence that the capsule stopped.
                if ($gameTime -le $arrival.LastTime) { return $null }
                $stepX = [double]$position['x'] - $arrival.LastX
                $stepZ = [double]$position['z'] - $arrival.LastZ
                $shift = [Math]::Sqrt($stepX * $stepX + $stepZ * $stepZ)
                $arrival.StableFrames = if ($shift -le .025) { $arrival.StableFrames + 1 } else { 0 }
                $arrival.LastTime = $gameTime
                $arrival.LastX = [double]$position['x']; $arrival.LastZ = [double]$position['z']
                if ($gameTime - $arrival.StartTime -lt .5 -or $arrival.StableFrames -lt 2) { return $null }
                if ($distance -le .20) { return $true }
                # The queued movement has stopped outside the target: correct with fresh feedback.
                $arrival.Active = $false
            }
            if ($distance -le .20) {
                Set-Neutral $Role "$Label-arrival-neutral"
                $arrival.Active = $true
                $arrival.StartTime = $gameTime; $arrival.LastTime = $gameTime
                $arrival.LastX = [double]$position['x']; $arrival.LastZ = [double]$position['z']
                $arrival.StableFrames = 0
                return $null
            }
            $yaw = [Math]::Atan2($dx, $dz) * 180 / [Math]::PI
            # Account for the 5Hz public view plus command transit before reaching the target.
            $axis = Get-WalkAxis $distance
            Set-Input $Role 0 $axis $yaw 0 $false $Label $position
            return $null
        }
    } finally { Set-Neutral $Role "$Label-stop" }
    Wait-GameSeconds .3
    $ended = (Get-LiveView $Role).Data
    $position = (Get-Miner $ended)['Position']
    $dx = $X - [double]$position['x']; $dz = $Z - [double]$position['z']
    Assert-Check ([Math]::Sqrt($dx * $dx + $dz * $dz) -le .45 -and
        [Math]::Abs([double]$position['y'] - $FloorY) -le $HeightTolerance) "$Role reached $Label using input"
    $evidence.Scenario.Movement.Add([ordered]@{ Role = $Role; Label = $Label; From = $from; To = $position;
        StartGameSeconds = $started['OnlineSeconds']; EndGameSeconds = $ended['OnlineSeconds'] })
}


function Item-Action([string]$Role, [string]$Action, [string]$ItemId, [bool]$Success = $true, $Direction = $null) {
    $item = Find-Entity (Get-LiveView $Role).Data 'Items' $ItemId
    return Invoke-Action $Role (New-Action $Action $Action $ItemId ([int]$item['Version']) $Direction) $Success
}
function Wait-Item([string]$Role, [string]$ItemId, [int]$Place, [string]$OwnerId) {
    return Wait-View $Role { param($view) @($view['Items'] | Where-Object {
        $_['Id'] -ceq $ItemId -and $_['Place'] -eq $Place -and $_['OwnerId'] -ceq $OwnerId }).Count -eq 1 }
}

$peerRoles = @('host', 'client1', 'client2', 'client3')
$evidence.Scenario['Topology'] = 'One host plus three separate local client processes on loopback; not four humans or four devices.'
$evidence.Scenario['Resources'] = [Collections.Generic.List[object]]::new()
$evidence.Scenario['InputIsolation'] = [Collections.Generic.List[object]]::new()
$evidence.Scenario['LateJoins'] = [Collections.Generic.List[object]]::new()
$evidence.Scenario['HostCrashRequested'] = [bool]$IncludeHostCrash
$evidence.Scenario['HostCrashPerformed'] = $false
$transfers = [Collections.Generic.List[object]]::new()

function Sample-Resources([string]$Label) {
    $rows = [Collections.Generic.List[object]]::new()
    foreach ($entry in $processes) {
        if ($entry.Process.HasExited) { continue }
        try {
            $entry.Process.Refresh()
            $rss = [long]$entry.Process.WorkingSet64
            $peak = [long]$entry.Process.PeakWorkingSet64
            if ($null -eq $entry.MaxObservedRss -or $rss -gt $entry.MaxObservedRss) { $entry.MaxObservedRss = $rss }
            $rows.Add(@{ Role = $entry.Role; Phase = $entry.Phase; Pid = $entry.Id; WorkingSetBytes = $rss; PeakWorkingSetBytes = $peak })
        } catch {
            $rows.Add(@{ Role = $entry.Role; Phase = $entry.Phase; Pid = $entry.Id; WorkingSetBytes = $null; ErrorType = $_.Exception.GetType().Name })
        }
    }
    $evidence.Scenario.Resources.Add(@{ Label = $Label; ElapsedSeconds = $suiteTimer.Elapsed.TotalSeconds; Processes = $rows.ToArray() })
}
function Distance-XZ($First, $Second) {
    $dx = [double]$First['x'] - [double]$Second['x']; $dz = [double]$First['z'] - [double]$Second['z']
    return [Math]::Sqrt($dx * $dx + $dz * $dz)
}
function Wait-FourViews([string]$Label) {
    $views = @{}
    foreach ($viewRole in $peerRoles) {
        $views[$viewRole] = Wait-View $viewRole { param($view)
            @($view['Players']).Count -eq 4 -and @($view['Players'] | Where-Object { $_['Online'] }).Count -eq 4 }
        Assert-Check ($views[$viewRole].Data['WorldId'] -ceq $worldId -and $views[$viewRole].Data['Self']['Id'] -ceq $playerIds[$viewRole]) "$Label $viewRole has the expected persistent identity and world"
        $ids = @($views[$viewRole].Data['Players'] | ForEach-Object { $_['Id'] } | Sort-Object -Unique)
        Assert-Check ($ids.Count -eq 4 -and @($ids | Where-Object { $_ -notin $playerIds.Values }).Count -eq 0) "$Label $viewRole sees exactly four distinct public identities"
        Assert-Projection $views[$viewRole] $playerIds[$viewRole] "$Label-$viewRole"
    }
    Assert-Check (@($processes | Where-Object { -not $_.Process.HasExited }).Count -eq 4) "$Label exactly four test player processes are running"
    return $views
}
function Assert-Accounts([string]$Label) {
    $observed = [Collections.Generic.List[long]]::new()
    foreach ($viewRole in $peerRoles) {
        $snapshot = Wait-View $viewRole { param($view)
            $view['Self']['Balance'] -eq $expectedBalances[$viewRole] -and @($view['Items'] | Where-Object { $_['Id'] -ceq 'a-ore' }).Count -eq 0 }
        Assert-Projection $snapshot $playerIds[$viewRole] "$Label-$viewRole"
        Assert-Check ($snapshot.Data['Self']['Balance'] -eq $expectedBalances[$viewRole]) "$Label $viewRole receives only its own correct balance"
        $observed.Add([long]$snapshot.Data['Self']['Balance'])
    }
    $sum = 0L; foreach ($amount in $observed) { $sum += $amount }
    Assert-Check ($sum -eq $saleAmount) "$Label total money equals exactly one fixture sale"
    Assert-Check (@($observed | Sort-Object -Unique).Count -eq 4) "$Label all four private balances are distinct"
}
function Replay-Receipt([string]$Role, $Command, $Expected, [string]$Label) {
    $existing = @(Read-Results $roles[$Role].Root | Where-Object { $_['RequestId'] -ceq $Command.RequestId }).Count
    Send-Command $Role $Command
    $reply = (Wait-Result $Role $Command.RequestId ($existing + 1)).Entries[$existing]
    Assert-ResultSame $reply $Expected $Label
}
function Assert-SavedState($State, [string]$Label) {
    Assert-Check ($State['WorldId'] -ceq $worldId -and @($State['Players']).Count -eq 4) "$Label retains one camp and four persistent identities"
    Assert-Check (@($State['Items']).Count -eq 1) "$Label did not respawn or duplicate the single A fixture item"
    foreach ($viewRole in $peerRoles) {
        $player = Find-Entity $State 'Players' $playerIds[$viewRole]
        Assert-Check ($player['Balance'] -eq $expectedBalances[$viewRole]) "$Label preserves $viewRole account"
    }
    $item = Find-Entity $State 'Items' 'a-ore'
    Assert-Check ($item['Place'] -eq 7 -and $item['OwnerId'] -ceq $playerIds[$winnerRole] -and $item['Version'] -eq $soldVersion -and $item['Quantity'] -eq 1) "$Label keeps exactly one sold ore with its final committed version"
    # Only this explicitly synthetic A fixture contains this known marker. Never
    # serialize the full host state or identity credentials into public evidence.
    Assert-Check ($item['HiddenValue'] -eq 12345 -and $item['CoreKind'] -ceq 'host-secret-test') "$Label retains the original host-only fixture marker"
    $saleReceipts = @($State['Receipts'] | Where-Object { $_['Key'] -ceq ($playerIds[$winnerRole] + ':' + $sale.RequestId) })
    Assert-Check ($saleReceipts.Count -eq 1) "$Label stores one durable sale receipt"
    Assert-ResultSame $saleReceipts[0]['Result'] $saleResult "$Label retains the confirmed sale response"
    foreach ($transfer in $transfers) {
        $receipts = @($State['Receipts'] | Where-Object { $_['Key'] -ceq ($playerIds[$winnerRole] + ':' + $transfer.Command.RequestId) })
        Assert-Check ($receipts.Count -eq 1 -and $receipts[0]['Result']['Success']) "$Label stores one receipt for each distinct transfer"
    }
}
function Exercise-OwnInput([string]$MovingRole, [double]$OffsetX, [double]$OffsetZ) {
    foreach ($neutralRole in $peerRoles) { Set-Neutral $neutralRole 'before isolated input' }
    Wait-GameSeconds .7
    $before = (Get-LiveView 'host').Data
    $positions = @{}
    foreach ($viewRole in $peerRoles) { $positions[$viewRole] = (Find-Entity $before 'Players' $playerIds[$viewRole])['Position'] }
    $p = $positions[$MovingRole]
    Move-To $MovingRole ([double]$p['x'] + $OffsetX) ([double]$p['z'] + $OffsetZ) "$MovingRole own input lane"
    Wait-GameSeconds .5
    $after = (Get-LiveView 'host').Data
    $moved = Distance-XZ $p (Find-Entity $after 'Players' $playerIds[$MovingRole])['Position']
    Assert-Check ($moved -ge .75 -and $moved -le 1.7) "$MovingRole own network input moved its actor" "distance=$moved"
    $others = [Collections.Generic.List[object]]::new()
    foreach ($viewRole in $peerRoles) {
        if ($viewRole -ceq $MovingRole) { continue }
        $delta = Distance-XZ $positions[$viewRole] (Find-Entity $after 'Players' $playerIds[$viewRole])['Position']
        Assert-Check ($delta -le .08) "$MovingRole input did not move $viewRole" "distance=$delta"
        $others.Add(@{ Role = $viewRole; Distance = $delta })
    }
    foreach ($observerRole in $peerRoles) {
        $snapshot = Wait-View $observerRole { param($view) [double]$view['OnlineSeconds'] -ge [double]$after['OnlineSeconds'] }
        $receivedPosition = (Find-Entity $snapshot.Data 'Players' $playerIds[$MovingRole])['Position']
        Assert-Check ((Distance-XZ $receivedPosition (Find-Entity $after 'Players' $playerIds[$MovingRole])['Position']) -le .15) "$observerRole received $MovingRole final input position"
    }
    $evidence.Scenario.InputIsolation.Add(@{ Role = $MovingRole; Distance = $moved; OtherActors = $others.ToArray() })
    Sample-Resources "after $MovingRole isolated input"
}

try {
    foreach ($role in $peerRoles) {
        $root = Assert-TestPath (Join-Path $runRoot $role)
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Write-AtomicText (Join-Path $root '.goldcamp-test-root') 'GoldCamp F isolated local process test with one synthetic A ore; not human play.'
        $roles[$role] = @{ Root = $root; InboxLines = [Collections.Generic.List[string]]::new(); CurrentProcess = $null; LastYaw = 0.0 }
    }
    $reservation = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $reservation.Client.ExclusiveAddressUse = $true
        $reservation.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
        $evidence.Port = ([Net.IPEndPoint]$reservation.Client.LocalEndPoint).Port
    } finally { $reservation.Dispose() }

    Start-Player 'host' 'first' $true
    $hostInitial = Wait-View 'host' { param($view) @($view['Items'] | Where-Object { $_['Id'] -ceq 'a-ore' }).Count -eq 1 }
    $worldId = [string]$hostInitial.Data['WorldId']
    $playerIds = @{ host = [string]$hostInitial.Data['Self']['Id'] }
    $expectedBalances = @{ host = 0L; client1 = 0L; client2 = 0L; client3 = 0L }
    Assert-Check ($hostInitial.Data['Self']['Balance'] -eq 0) 'Host starts with no injected money'
    $initialOre = Find-Entity $hostInitial.Data 'Items' 'a-ore'
    Assert-Check ($initialOre['Place'] -eq 0 -and $initialOre['Kind'] -eq 2 -and $initialOre['Quantity'] -eq 1) 'Only the normal A test ore funds this scenario'
    $null = Item-Action 'host' 'pickup' 'a-ore'
    Move-To 'host' 0 -3 'host positions held ore before late joins'
    $null = Item-Action 'host' 'drop' 'a-ore' $true ([ordered]@{ x = 0; y = 0; z = 1 })
    Wait-GameSeconds .8
    $prepared = Find-Entity (Get-LiveView 'host').Data 'Items' 'a-ore'
    Assert-Check ($prepared['Place'] -eq 0 -and $prepared['Version'] -eq [int]$initialOre['Version'] + 2) 'Host committed pickup and drop before anyone joined'
    Move-To 'host' -1.8 -3.2 'host contention lane'
    $laneX = @{ client1 = -.6; client2 = .6; client3 = 1.8 }
    $joinedCount = 1
    foreach ($role in @('client1', 'client2', 'client3')) {
        Start-Player $role 'first' $false
        $joinedCount++
        $joined = Wait-View $role { param($view) @($view['Players'] | Where-Object { $_['Online'] }).Count -eq $joinedCount }
        $playerIds[$role] = [string]$joined.Data['Self']['Id']
        $lateOre = Find-Entity $joined.Data 'Items' 'a-ore'
        Assert-Check ($joined.Data['WorldId'] -ceq $worldId -and $joined.Data['Self']['Balance'] -eq 0 -and $lateOre['Place'] -eq 0 -and
            $lateOre['Version'] -eq $prepared['Version']) "$role late join sees the already committed ore version and its own empty account"
        Assert-Projection $joined $playerIds[$role] "late-join-$role"
        $evidence.Scenario.LateJoins.Add(@{ Role = $role; PlayerId = $playerIds[$role]; PublicPlayers = $joinedCount; OreVersion = $lateOre['Version'] })
        Move-To $role $laneX[$role] -3.2 "$role contention lane"
    }
    Assert-Check (@($playerIds.Values | Sort-Object -Unique).Count -eq 4 -and @($playerIds.Values | Where-Object { $_ -notmatch '^[0-9a-f]{32}$' }).Count -eq 0) 'Four marked profile roots produced four distinct stable identities'
    $initialViews = Wait-FourViews 'four-ready'
    Sample-Resources 'four peers ready'
    $ore = Find-Entity $initialViews.host.Data 'Items' 'a-ore'
    foreach ($role in $peerRoles) {
        $sameOre = Find-Entity $initialViews[$role].Data 'Items' 'a-ore'
        Assert-Check ($sameOre['Place'] -eq 0 -and $sameOre['Version'] -eq $ore['Version']) "$role contends against the same real ground item version"
    }
    $pickup = New-Command 'f-four-way-pickup' 'pickup' 'a-ore' ([int]$ore['Version'])
    $dispatch = [Diagnostics.Stopwatch]::StartNew()
    foreach ($role in $peerRoles) { Send-Command $role $pickup }
    $dispatch.Stop()
    $evidence.Scenario['PickupDispatchMilliseconds'] = $dispatch.Elapsed.TotalMilliseconds
    $raceResults = @{}
    foreach ($role in $peerRoles) { $raceResults[$role] = (Wait-Result $role $pickup.RequestId).Entries[0] }
    $winners = @($peerRoles | Where-Object { $raceResults[$_]['Success'] })
    Assert-Check ($winners.Count -eq 1) 'Four outstanding same-version pickup requests have exactly one winner'
    $winnerRole = $winners[0]
    foreach ($role in $peerRoles) {
        if ($role -cne $winnerRole) { Assert-Check ($raceResults[$role]['Code'] -eq 'stale') "$role losing pickup is rejected without changing ownership" }
        $held = Wait-Item $role 'a-ore' 1 $playerIds[$winnerRole]
        Assert-Projection $held $playerIds[$role] "race-winner-$role"
    }
    $heldOre = Find-Entity (Get-LiveView $winnerRole).Data 'Items' 'a-ore'
    $saleAmount = [long]$heldOre['SaleQuote']; $soldVersion = [int]$heldOre['Version'] + 1
    Assert-Check ($saleAmount -gt 12) 'One fixture sale can fund four distinct normal accounts'
    $sale = New-Command 'f-sell-once' 'sell' 'a-ore' ([int]$heldOre['Version'])
    $saleResult = Invoke-Action $winnerRole $sale
    Assert-Check ($saleResult['Amount'] -eq $saleAmount) 'Winner sold the contested ore once for its visible quote'
    $expectedBalances[$winnerRole] += $saleAmount
    Replay-Receipt $winnerRole $sale $saleResult 'Immediate sale replay returns the original confirmed receipt'
    $amount = 1L
    foreach ($role in $peerRoles) {
        if ($role -ceq $winnerRole) { continue }
        $command = New-Command "f-transfer-$role" 'transfer' '' 0 $playerIds[$role] $amount
        $result = Invoke-Action $winnerRole $command
        Assert-Check ($result['Amount'] -eq $amount) 'Normal transfer distributes only the requested coins'
        $expectedBalances[$winnerRole] -= $amount; $expectedBalances[$role] += $amount
        $transfers.Add(@{ Command = $command; Result = $result })
        $amount++
    }
    Assert-Accounts 'distinct-private-accounts'
    Sample-Resources 'after four-way contention and settlement'
    Exercise-OwnInput 'host' -1.2 0
    Exercise-OwnInput 'client1' 0 -1.2
    Exercise-OwnInput 'client2' 0 1.2
    Exercise-OwnInput 'client3' 1.2 0

    $reconnectRole = if ($winnerRole -ne 'host') { $winnerRole } else { 'client2' }
    $oldPid = $roles[$reconnectRole].CurrentProcess.Id
    Stop-Player $reconnectRole
    $null = Wait-View 'host' { param($view) @($view['Players'] | Where-Object { $_['Online'] }).Count -eq 3 }
    Assert-Check (-not $roles.host.CurrentProcess.Process.HasExited) 'Host stayed running during client process replacement'
    Archive-ControlFiles $reconnectRole 'before-client-reconnect'
    Start-Player $reconnectRole 'rejoined' $false
    $rejoined = Wait-FourViews 'client-rejoined'
    Assert-Check ($roles[$reconnectRole].CurrentProcess.Id -ne $oldPid -and $rejoined[$reconnectRole].Data['Self']['Id'] -ceq $playerIds[$reconnectRole]) 'Replacement client process recovered the same profile identity'
    Assert-Accounts 'accounts-after-client-reconnect'
    Replay-Receipt $winnerRole $sale $saleResult 'Sale replay stays idempotent after a real client process reconnect'
    Assert-Accounts 'accounts-after-client-replay'
    Sample-Resources 'after client process reconnect'

    if ($IncludeHostCrash) {
        # The only intentional process termination is this already-owned test host
        # handle, after sale and transfer acknowledgements proved durable commits.
        $hostEntry = $roles.host.CurrentProcess
        $hostEntry.ExpectedRunning = $false; $hostEntry.IntentionalCrash = $true
        $evidence.Scenario.HostCrashPerformed = $true
        $evidence.Scenario['IntentionalCrash'] = @{ Pid = $hostEntry.Id; Phase = $hostEntry.Phase; AfterConfirmedSale = $sale.RequestId; Utc = [DateTime]::UtcNow.ToString('O') }
        $hostEntry.Process.Kill()
        if (-not $hostEntry.Process.WaitForExit(10000)) { throw 'The specifically owned test host did not terminate within its fault-injection deadline.' }
        Assert-Check ($hostEntry.Process.HasExited -and $hostEntry.IntentionalCrash -and -not $hostEntry.Forced) 'Intentional host crash is distinct from forced cleanup'
        foreach ($role in @('client1', 'client2', 'client3')) { Stop-Player $role }
    } else {
        foreach ($role in @('client1', 'client2', 'client3')) { Stop-Player $role }
        Wait-GameSeconds .5
        Stop-Player 'host'
    }
    Assert-SavedState (Read-World $worldId) 'before host reopen'
    foreach ($role in $peerRoles) { Archive-ControlFiles $role 'before-host-reopen' }
    Start-Player 'host' 'reopened' $false
    $null = Get-LiveView 'host'
    foreach ($role in @('client1', 'client2', 'client3')) { Start-Player $role 'reopened' $false }
    $null = Wait-FourViews 'host-reopened'
    Assert-Accounts 'accounts-after-host-reopen'
    Replay-Receipt $winnerRole $sale $saleResult 'Sale receipt survives host process replacement with no second payout'
    foreach ($transfer in $transfers) { Replay-Receipt $winnerRole $transfer.Command $transfer.Result 'Confirmed transfer replays once after host replacement' }
    Assert-Accounts 'final-private-accounts'
    Sample-Resources 'four peers after host reopen'
    foreach ($role in @('client1', 'client2', 'client3')) { Stop-Player $role }
    Wait-GameSeconds .5
    Stop-Player 'host'
    Assert-SavedState (Read-World $worldId) 'final clean save'
    foreach ($entry in $processes) {
        $errors = @(Select-String -LiteralPath $entry.LogPath -Pattern 'NullReferenceException|MissingReferenceException|InvalidOperationException|IndexOutOfRangeException|GOLDCAMP.*(FAILED|ERROR)' -ErrorAction Stop)
        Assert-Check ($errors.Count -eq 0) "$($entry.Role)/$($entry.Phase) has no matching runtime exceptions" 'Counts and log paths only; no credential or save payload contents copied.'
    }
    $evidence.Scenario['WorldId'] = $worldId
    $evidence.Scenario['PlayerIds'] = $playerIds
    $evidence.Scenario['WinnerRole'] = $winnerRole
    $evidence.Scenario['DistinctPrivateBalanceCount'] = 4
    $evidence.Scenario['TotalMoneyFromSingleSale'] = $saleAmount
    $evidence.Scenario['SaleResult'] = $saleResult
    $evidence.Scenario['ElapsedSeconds'] = $suiteTimer.Elapsed.TotalSeconds
    $evidence.Success = $true
} catch {
    $evidence.Error = $_.Exception.Message
    $evidence.Scenario['FailureLine'] = $_.InvocationInfo.ScriptLineNumber
} finally {
    # Stop only handles created by this script. Never identify processes by name.
    foreach ($entry in $processes) {
        if (-not $entry.Process.HasExited) {
            $entry.ExpectedRunning = $false
            try { Set-Neutral $entry.Role 'failure-neutral'; Send-Command $entry.Role (New-Command 'f-cleanup-shutdown' 'shutdown') } catch { }
        }
    }
    $cleanup = [Diagnostics.Stopwatch]::StartNew()
    while ($cleanup.Elapsed.TotalSeconds -lt 10 -and @($processes | Where-Object { -not $_.Process.HasExited }).Count -gt 0) {
        Start-Sleep -Milliseconds 100
    }
    foreach ($entry in $processes) {
        if (-not $entry.Process.HasExited) {
            $entry.Forced = $true
            $evidence.Success = $false
            try { $entry.Process.Kill(); $null = $entry.Process.WaitForExit(5000) } catch { }
        }
        $exitCode = if ($entry.Process.HasExited) { $entry.Process.ExitCode } else { $null }
        $evidence.Processes += [ordered]@{
            Role = $entry.Role; Phase = $entry.Phase; Pid = $entry.Id; StartedUtc = $entry.StartedUtc
            ExitCode = $exitCode; ForcedCleanup = $entry.Forced; IntentionalCrash = $entry.IntentionalCrash; MaximumObservedWorkingSetBytes = $entry.MaxObservedRss; LogPath = $entry.LogPath; TestRoot = $entry.Root
        }
    }
    $evidence.FinishedUtc = [DateTime]::UtcNow.ToString('O')
    $evidencePath = Join-Path $runRoot 'evidence.json'
    Write-AtomicText $evidencePath (ConvertTo-Json -InputObject $evidence -Depth 40)
}
[pscustomobject]@{ Success = $evidence.Success; EvidencePath = $evidencePath; RunRoot = $runRoot; Checks = $checks.Count; Port = $evidence.Port }
if (-not $evidence.Success) { throw "Demo four-peer validation failed. Evidence: $evidencePath" }
