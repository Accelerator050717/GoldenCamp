#requires -Version 7.0
<#
.SYNOPSIS
Exercises checkpoint D using two real FishNet players and networked movement axes.
.DESCRIPTION
Creates fresh, marked test roots under .local/TestRuns; never accepts a save path.
CampTestDriver consumes inbox.jsonl. commands.jsonl is an identical audit copy.
Mines finite public cells, earns the upgrade and shared repair, drives cargo and reopens the camp.
Uses an ordinary seed-6 new camp. No fixture, balance injection, teleport, or hidden-yield selection.
This is automated loopback validation, not human input, LAN, Steam, or voice testing.
Use -ValidateInputMathOnly to check movement calculations without files or player processes.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 300)] [int]$TimeoutSeconds = 90,
    [switch]$WithGraphics,
    [switch]$ShowHostWindow,
    [switch]$ValidateInputMathOnly
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
if ($ShowHostWindow -and -not $WithGraphics) { throw '-ShowHostWindow requires -WithGraphics.' }

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
    Suite = 'D-earned-progression-input-two-process-loopback'
    Mode = $(if ($WithGraphics) { 'Automated player processes with graphics' } else { 'Automated -batchmode -nographics player processes' })
    NotValidated = @('human input', 'visual readability', 'LAN devices', 'cross-network play', 'Steam', 'voice', 'four humans', 'vehicle recovery blocked geometry')
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
        $text = Read-SharedText (Assert-TestPath $Path)
        if ([string]::IsNullOrWhiteSpace($text)) { return $null }
        return @{ Data = (ConvertFrom-Json -InputObject $text -AsHashtable -Depth 100); Raw = $text }
    } catch {
        # view.json is rewritten by the player. Missing/locked/partial JSON is retried.
        return $null
    }
}

function Read-SharedText([string]$Path) {
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    try {
        $reader = [IO.StreamReader]::new($stream, $utf8)
        try { return $reader.ReadToEnd() } finally { $reader.Dispose() }
    } finally { $stream.Dispose() }
}

function Read-Results([string]$Root) {
    try { $text = Read-SharedText (Assert-TestPath (Join-Path $Root 'results.jsonl')) }
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
    if ($suiteTimer.Elapsed.TotalMinutes -gt 45) { throw 'Progression suite exceeded its 45-minute hard deadline.' }
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
    if ($ShowHostWindow -and $Role -eq 'host') { $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal }
    $arguments = [Collections.Generic.List[string]]::new()
    if (-not $WithGraphics) { $arguments.Add('-batchmode'); $arguments.Add('-nographics') }
    foreach ($argument in @('-logFile', $logPath, '--gc-test-root', $root, '--gc-port', [string]$evidence.Port)) {
        $arguments.Add($argument)
    }
    if ($Role -eq 'host') {
        $arguments.Add('--gc-host')
        if ($NewWorld) { $arguments.Add('--gc-new'); $arguments.Add('--gc-seed'); $arguments.Add([string]$progressionSeed) }
    } else {
        $arguments.Add('--gc-connect'); $arguments.Add('127.0.0.1')
    }
    $arguments.Add('--gc-auto-input')
    if ($WithGraphics) {
        foreach ($argument in @('-screen-fullscreen','0','-screen-width','960','-screen-height','600')) { $arguments.Add($argument) }
    }
    Set-Input $Role 0 0 0 0 $false 'launch-neutral'
    foreach ($argument in $arguments) { $start.ArgumentList.Add($argument) }
    $playerProcess = [Diagnostics.Process]::Start($start)
    if ($null -eq $playerProcess) { throw "Could not start $Role player." }
    $entry = [pscustomobject]@{
        Role = $Role; Phase = $Phase; Root = $root; Process = $playerProcess
        Id = $playerProcess.Id; StartedUtc = [DateTime]::UtcNow.ToString('O')
        LogPath = $logPath; ExpectedRunning = $true; Forced = $false
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
    Send-Command $Role (New-Command "d-shutdown-$Role-$($entry.Phase)" 'shutdown')
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


$progressionSeed = 6
$commandNumber = 0
$evidence.Scenario['Seed'] = $progressionSeed
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
    $command = New-Command ("d-$Label-" + $script:commandNumber) $Action $Target $Version
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

$bankSide = ''
$atMaintenance = $false
$minedIds = [Collections.Generic.List[string]]::new()
$replays = [Collections.Generic.List[object]]::new()
$evidence.Scenario['Harvest'] = [Collections.Generic.List[object]]::new()
$evidence.Scenario['Driving'] = [Collections.Generic.List[object]]::new()
$evidence.Scenario['ResourcePolicy'] = 'Fixed geometric cell order, at most 50 starter cells; inspect only public yields after digging. No fixture or save reads during play.'

function Distance-XZ($First, $Second) {
    $dx = [double]$First['x'] - [double]$Second['x']; $dz = [double]$First['z'] - [double]$Second['z']
    return [Math]::Sqrt($dx * $dx + $dz * $dz)
}
function Assert-Balances([string]$Label) {
    foreach ($accountRole in @('host', 'client')) {
        $balance = [long](Get-LiveView $accountRole).Data['Self']['Balance']
        Assert-Check ($balance -eq $expectedBalances[$accountRole]) "$Label $accountRole exact balance" "expected=$($expectedBalances[$accountRole]); actual=$balance"
    }
}
function Remember-Receipt([string]$Role, $Command, $Result) {
    if (@($replays | Where-Object { $_.Command.Action -ceq $Command.Action }).Count -eq 0) {
        $replays.Add(@{ Role = $Role; Command = $Command; Result = $Result })
    }
}
function Get-Carried([string]$Role) {
    $view = (Get-LiveView $Role).Data
    return @($view['Items'] | Where-Object { $_['OwnerId'] -ceq $playerIds[$Role] -and $_['Place'] -in @(1, 2) })
}
function Count-Material([string]$Role, [int]$Kind) {
    $total = 0
    foreach ($item in @(Get-Carried $Role)) { if ($item['Kind'] -eq $Kind) { $total += [int]$item['Quantity'] } }
    return $total
}
function Sell-Carried([string]$Role, [string]$ItemId) {
    $before = Find-Entity (Get-LiveView $Role).Data 'Items' $ItemId
    $command = New-Action 'earned-sale' 'sell' $ItemId ([int]$before['Version'])
    $result = Invoke-Action $Role $command
    Assert-Check ($result['Amount'] -eq [long]$before['SaleQuote']) "$ItemId paid its public quote"
    $expectedBalances[$Role] += [long]$result['Amount']
    Remember-Receipt $Role $command $result
    Assert-Balances 'sale affects only the seller'
}
function Contribute([string]$Role, [bool]$Iron, [long]$Amount) {
    $before = (Get-LiveView $Role).Data
    $action = if ($Iron) { 'contribute_iron' } else { 'contribute_coins' }
    $command = New-Action 'voluntary-part' $action '' ([int]$before['RoadVersion'])
    $command.Amount = $Amount
    $result = Invoke-Action $Role $command
    if (-not $Iron) { $expectedBalances[$Role] -= $Amount }
    Remember-Receipt $Role $command $result
    foreach ($viewRole in @('host', 'client')) {
        $after = Wait-View $viewRole { param($view) [int]$view['RoadVersion'] -ge [int]$before['RoadVersion'] + 1 }
        $field = if ($Iron) { 'RoadIronPaid' } else { 'RoadCoinsPaid' }
        Assert-Check ($after.Data[$field] -eq [long]$before[$field] + $Amount) "$Role partial $action replicated to $viewRole"
    }
    Assert-Balances 'voluntary contribution never charges the other player'
}
function Ensure-LadderStock {
    while ([int](Get-LiveView 'host').Data['Self']['LadderStock'] -lt 4 -and (Count-Material 'host' 0) -ge 2) {
        $before = [int](Get-LiveView 'host').Data['Self']['LadderStock']
        $null = Invoke-Action 'host' (New-Action 'earned-ladder' 'craft_ladder')
        Assert-Check ([int](Get-LiveView 'host').Data['Self']['LadderStock'] -eq $before + 1) 'Two normal stones crafted one ladder'
    }
}
function Mining-GoalMet {
    $view = (Get-LiveView 'host').Data
    return $view['RoadUnlocked'] -and [int]$view['Self']['ToolLevel'] -eq 2
}
function Leave-Bank {
    if ([string]::IsNullOrEmpty($script:bankSide)) { return }
    $p = (Get-Miner (Get-LiveView 'host').Data)['Position']
    switch ($script:bankSide) {
        'South' { Move-To 'host' $p['x'] 18 'south outer lane' }
        'East' { Move-To 'host' 8 $p['z'] 'east outer lane' }
        'North' { Move-To 'host' $p['x'] 34 'north outer lane'; Move-To 'host' 8 34 'northeast outer corner' }
        'West' { Move-To 'host' -8 $p['z'] 'west outer lane'; Move-To 'host' -8 18 'southwest outer corner' }
    }
    Move-To 'host' 8 18 'starter safe hub'
    $script:bankSide = ''
}
function Go-Bank([int]$X, [int]$Z, [string]$Side) {
    $cx = -5 + 2 * $X; $cz = 21 + 2 * $Z
    if ($script:bankSide -cne $Side) {
        Leave-Bank
        if ($script:atMaintenance) {
            Move-To 'host' 17.8 4 'maintenance north lane'
            Move-To 'host' 16 4 'west of roadside trees'
            Move-To 'host' 8 8 'camp open north lane'
            Move-To 'host' 8 18 'starter approach'
            $script:atMaintenance = $false
        }
        switch ($Side) {
            'South' { Move-To 'host' $cx 18 'south cell lane' }
            'East' { Move-To 'host' 8 $cz 'east cell lane' }
            'North' { Move-To 'host' 8 34 'northeast approach'; Move-To 'host' $cx 34 'north cell lane' }
            'West' { Move-To 'host' -8 18 'southwest approach'; Move-To 'host' -8 $cz 'west cell lane' }
        }
    }
    switch ($Side) {
        'South' { Move-To 'host' $cx 19.25 "south bank x$X" }
        'East' { Move-To 'host' 6.75 $cz "east bank z$Z" }
        'North' { Move-To 'host' $cx 32.75 "north bank x$X" }
        'West' { Move-To 'host' -6.75 $cz "west bank z$Z" }
    }
    $script:bankSide = $Side
}
function Service-Camp {
    Leave-Bank
    if ($script:atMaintenance) {
        Move-To 'host' 17.8 4 'maintenance return north'
        Move-To 'host' 16 4 'tree clearance'
        Move-To 'host' 8 8 'return open camp lane'
    }
    Move-To 'host' 2.2 2 'sale counter southeast'
    Ensure-LadderStock
    foreach ($item in @(Get-Carried 'host')) {
        $view = (Get-LiveView 'host').Data
        if ($item['Kind'] -eq 1 -and [int]$view['RoadIronPaid'] -lt [int]$view['RoadRequiredIron']) { continue }
        if ($item['Kind'] -eq 0 -and [int]$view['Self']['LadderStock'] -lt 4) { continue }
        Sell-Carried 'host' $item['Id']
    }
    $view = (Get-LiveView 'host').Data
    if ([int]$view['Self']['ToolLevel'] -lt 2 -and [long]$view['Self']['Balance'] -ge [long]$rules['UpgradePrice']) {
        Move-To 'host' -4 2 'personal repair bench approach'
        $command = New-Action 'earned-pickaxe' 'upgrade_tool' '' ([int]$view['Self']['ToolLevel'])
        $result = Invoke-Action 'host' $command
        $expectedBalances.host -= [long]$result['Amount']
        Remember-Receipt 'host' $command $result
        Assert-Check ([int](Get-LiveView 'host').Data['Self']['ToolLevel'] -eq 2) 'Host bought pickaxe level two using earned money'
        Assert-Balances 'personal equipment does not charge passenger'
    }
    Move-To 'host' 8 8 'public works via open camp lane'
    Move-To 'host' 16 4 'public works west approach'
    Move-To 'host' 17.8 4 'maintenance side lane'
    Move-To 'host' 17.8 -7 'maintenance side clear of parked truck'
    $script:atMaintenance = $true
    $view = (Get-LiveView 'host').Data
    $iron = [Math]::Min([int]$view['RoadRequiredIron'] - [int]$view['RoadIronPaid'], (Count-Material 'host' 1))
    if ($iron -gt 0) { Contribute 'host' $true $iron }
    $view = (Get-LiveView 'host').Data
    $reserve = if ([int]$view['Self']['ToolLevel'] -lt 2) { [long]$rules['UpgradePrice'] } else { 0L }
    $coins = [Math]::Min([long]$view['RoadRequiredCoins'] - [long]$view['RoadCoinsPaid'], [Math]::Max(0L, [long]$view['Self']['Balance'] - $reserve))
    if ($coins -gt 0) { Contribute 'host' $false $coins }
}
function Mine-Cell([string]$CellId) {
    $hits = 0
    while ($hits -lt 30) {
        Wait-GameSeconds ([double]$rules['MineInterval'] + .22)
        $view = (Get-LiveView 'host').Data
        $cell = Find-Entity $view 'Cells' $CellId
        if ($cell['Dug']) { break }
        $ox = if ($cell['PitId'] -eq 'starter') { -6 } else { 18 }
        $oz = if ($cell['PitId'] -eq 'starter') { 20 } else { 48 }
        $cx = $ox + (1 + 2 * [int]$cell['X']); $cy = -1 - 2 * [int]$cell['Depth']; $cz = $oz + 1 + 2 * [int]$cell['Z']
        $direction = Aim-At (Get-Miner $view)['Position'] $cx $cy $cz
        $null = Invoke-Action 'host' (New-Action 'finite-mining' 'mine' $CellId ([int]$cell['Version']) $direction)
        $after = Find-Entity (Get-LiveView 'host').Data 'Cells' $CellId
        Assert-Check ($after['Version'] -eq [int]$cell['Version'] + 1 -and [double]$after['Health'] -lt [double]$cell['Health']) "$CellId hit changes one cell version and health"
        $hits++
        if ($after['Dug']) { break }
    }
    $cell = Find-Entity (Get-LiveView 'host').Data 'Cells' $CellId
    Assert-Check ($cell['Dug'] -and $hits -gt 0) "$CellId mined by actual repeated commands" "hits=$hits"
    $minedIds.Add($CellId)
    Wait-GameSeconds .7
    $itemId = "yield:$($CellId):0"
    $item = Find-Entity (Get-LiveView 'host').Data 'Items' $itemId
    Assert-Check ($item['Place'] -eq 0 -and $item['Quantity'] -eq 1) "$CellId exposes one unique public yield"
    $evidence.Scenario.Harvest.Add(@{ CellId = $CellId; ItemId = $itemId; Kind = $item['Kind']; Quote = $item['SaleQuote']; Hits = $hits })
    return $itemId
}
function Place-Ladder([string]$CellId) {
    $cell = Find-Entity (Get-LiveView 'host').Data 'Cells' $CellId
    $null = Invoke-Action 'host' (New-Action 'place-ladder' 'place_ladder' $CellId ([int]$cell['Version']))
}
function Attach-Ladder([string]$CellId) {
    $ladder = Find-Entity (Get-LiveView 'host').Data 'Ladders' "ladder:$CellId"
    $null = Invoke-Action 'host' (New-Action 'attach' 'climb' $ladder['Id'] ([int]$ladder['Version']))
    $null = Wait-View 'host' { param($view) (Get-Miner $view)['ClimbingLadderId'] -ceq $ladder['Id'] }
}
function Remove-Ladder([string]$CellId) {
    $ladder = Find-Entity (Get-LiveView 'host').Data 'Ladders' "ladder:$CellId"
    $null = Invoke-Action 'host' (New-Action 'recover-ladder' 'remove_ladder' $ladder['Id'] ([int]$ladder['Version']))
}
function Climb-To([double]$Y, [string]$Label) {
    $arrivalTolerance = if ($Y -ge 0) { .018 } else { .12 }
    try {
        $null = Wait-Until "real ladder input $Label to y=$Y" {
            $snapshot = Read-JsonSnapshot (Join-Path $roles.host.Root 'view.json')
            if ($null -eq $snapshot) { return $null }
            $miner = Get-Miner $snapshot.Data
            if ([string]::IsNullOrEmpty($miner['ClimbingLadderId'])) { throw "Lost ladder while climbing $Label." }
            $position = $miner['Position']; $delta = $Y - [double]$position['y']
            if ([Math]::Abs($delta) -le $arrivalTolerance) { return $true }
            Set-Input 'host' 0 (Get-ClimbAxis $delta) 0 0 $false $Label $position
            return $null
        }
    } finally { Set-Neutral 'host' "$Label-stop" }
    Assert-Check ([Math]::Abs([double](Get-Miner (Get-LiveView 'host').Data)['Position']['y'] - $Y) -le .20) "$Label reached height by climbing"
}
function Exit-LadderNorth([double]$X, [double]$Z) {
    $command = New-Action 'top-unclip' 'unclip'
    Send-Command 'host' $command
    Set-Input 'host' 0 1 0 0 $false 'north-off-top'
    try {
        $result = (Wait-Result 'host' $command.RequestId).Entries[0]
        Assert-Check ($result['Success']) 'Unclip before stepping onto intact north surface'
        # As in checkpoint B, do not reduce forward input until the heavy
        # carrier is fully over the rim. Flat-ground braking is premature here.
        $null = Wait-Until 'carrier steps fully onto the intact north surface' {
            $p = (Get-Miner (Get-LiveView 'host').Data)['Position']
            if ([double]$p['y'] -lt -.75) { throw 'Carrier fell while holding full forward input onto the north surface.' }
            return [double]$p['z'] -ge ($Z - .35) -and [double]$p['y'] -gt -.1
        }
        Set-Neutral 'host' 'north-rim-reached'
        Move-To 'host' $X $Z 'north ladder exit' 0 .75
    } finally { Set-Neutral 'host' 'exit-ladder-stop' }
}
function Harvest-Starter($Candidate) {
    $x = [int]$Candidate.X; $z = [int]$Candidate.Z; $depth = [int]$Candidate.Depth
    $id = "starter:$($x):$($depth):$z"
    Go-Bank $x $z $Candidate.Side
    if ($depth -gt 0) {
        # The ladder sits only .25 m inside the north wall. Step to the supported
        # lip so the eye-to-ladder ray enters the open shaft before crossing y=0.
        if ($Candidate.Side -eq 'North') { Move-To 'host' (-5 + 2 * $x) 32.1 'north ladder lip' }
        Assert-Check ([int](Get-LiveView 'host').Data['Self']['LadderStock'] -ge $depth + 1) 'Enough normally crafted ladders for this bounded depth'
        for ($d = 0; $d -lt $depth; $d++) {
            if ($d -gt 0) { Climb-To (-2 * $d + .15) 'reach next excavated segment' }
            $upper = "starter:$($x):$($d):$z"
            Place-Ladder $upper; Attach-Ladder $upper
        }
        Climb-To (-2 * $depth + .15) 'reach intact target ceiling'
        $null = Invoke-Action 'host' (New-Action 'unclip-for-mine' 'unclip')
        Wait-GameSeconds .4
    }
    $itemId = Mine-Cell $id
    $null = Item-Action 'host' 'pickup' $itemId
    $item = Find-Entity (Get-LiveView 'host').Data 'Items' $itemId
    if ($item['Kind'] -ne 3) { $null = Item-Action 'host' 'pocket' $itemId }
    if ($depth -gt 0) {
        Place-Ladder $id; Attach-Ladder $id
        for ($d = $depth; $d -gt 0; $d--) {
            Climb-To (-2 * $d + .15) 'ascending to supported upper segment'
            Attach-Ladder "starter:$($x):$($d - 1):$z"
            Remove-Ladder "starter:$($x):$($d):$z"
        }
        Climb-To .1 'surface ladder top'
        if ($Candidate.Side -eq 'North') {
            Exit-LadderNorth (-5 + 2 * $x) 32.8
            Move-To 'host' (-5 + 2 * $x) 32.1 'north lip to recover ladder'
            Remove-Ladder "starter:$($x):0:$z"
        } else {
            # South interior columns retain an intact interior strip to the north row.
            Exit-LadderNorth (-5 + 2 * $x) 22.8
            Remove-Ladder "starter:$($x):0:$z"
            Move-To 'host' (-5 + 2 * $x) 29.25 'intact interior northbound strip'
            $north = "starter:$($x):0:5"
            Place-Ladder $north; Attach-Ladder $north; Climb-To .1 'north perimeter crossing'
            Exit-LadderNorth (-5 + 2 * $x) 32.8
            Move-To 'host' (-5 + 2 * $x) 32.1 'north lip to recover crossing ladder'
            Remove-Ladder $north
        }
        $script:bankSide = 'North'
        Assert-Check (@((Get-LiveView 'host').Data['Ladders']).Count -eq 0) 'Every placed segment was recovered once after leaving the shaft'
    }
    Ensure-LadderStock
}

function Vehicle-Action([string]$Role, [string]$Action, [int]$Amount = 0) {
    $car = (Get-LiveView $Role).Data['Vehicle']
    $command = New-Action 'truck' $Action $car['Id'] ([int]$car['Version'])
    $command.Amount = $Amount
    return Invoke-Action $Role $command
}
function Cargo-Action([string]$Action, [string]$ItemId, [int]$Slot = 0) {
    $item = Find-Entity (Get-LiveView 'host').Data 'Items' $ItemId
    $command = New-Action 'cargo' $Action $ItemId ([int]$item['Version'])
    $command.Amount = $Slot
    $null = Invoke-Action 'host' $command
}
function Assert-Seats([bool]$Seated) {
    foreach ($viewRole in @('host', 'client')) {
        $view = Wait-View $viewRole { param($data)
            ($data['Vehicle']['Seats'][0] -ceq $playerIds.host) -eq $Seated -and
            ($data['Vehicle']['Seats'][1] -ceq $playerIds.client) -eq $Seated }
        Assert-Check (((Find-Entity $view.Data 'Players' $playerIds.host)['VehicleSeat'] -eq 0) -eq $Seated) "$viewRole receives driver seat state"
        Assert-Check (((Find-Entity $view.Data 'Players' $playerIds.client)['VehicleSeat'] -eq 1) -eq $Seated) "$viewRole receives passenger seat state"
    }
}
function Wait-Stopped([string]$Label) {
    Set-Neutral 'host' "$Label-brake"
    $start = [double](Get-LiveView 'host').Data['OnlineSeconds']
    $null = Wait-View 'host' { param($view) [Math]::Abs([double]$view['Vehicle']['Speed']) -le .02 -and [double]$view['OnlineSeconds'] -ge $start + .7 }
}
function Get-AngleError([double]$Desired, [double]$Actual) {
    return (($Desired - $Actual + 540.0) % 360.0) - 180.0
}
function Drive-To([double]$X, [double]$Z, [string]$Label, [bool]$Reverse = $false, [double]$MaximumSpeed = 4.5) {
    $initialCar = (Get-LiveView 'host').Data['Vehicle']
    $samples = [Collections.Generic.List[object]]::new()
    $state = @{ LastTime = -1.0; Arriving = $false; Since = 0.0; BestDistance = [double]::PositiveInfinity; LastProgressTime = -1.0 }
    try {
        $null = Wait-Until "driving $Label to ($X,$Z), no coordinate edits" {
            $snapshot = Read-JsonSnapshot (Join-Path $roles.host.Root 'view.json')
            if ($null -eq $snapshot) { return $null }
            $view = $snapshot.Data; $car = $view['Vehicle']; $time = [double]$view['OnlineSeconds']
            if ($time -le $state.LastTime) { return $null }
            $state.LastTime = $time
            if ($car['Seats'][0] -cne $playerIds.host -or $car['Seats'][1] -cne $playerIds.client) { throw 'Lost voluntary driver/passenger seats during travel.' }
            $p = $car['Position']; $dx = $X - [double]$p['x']; $dz = $Z - [double]$p['z']
            $distance = [Math]::Sqrt($dx * $dx + $dz * $dz)
            if ([Math]::Abs([double]$p['y']) -gt .3) { throw "Truck left supported road: $($p | ConvertTo-Json -Compress)" }
            $samples.Add(@{ Seconds = $time; Position = $p; Yaw = $car['Yaw']; Speed = $car['Speed'] })
            if ($distance -lt $state.BestDistance - .2) { $state.BestDistance = $distance; $state.LastProgressTime = $time }
            if ($state.LastProgressTime -ge 0 -and $time - $state.LastProgressTime -gt 15) {
                throw "Truck cannot approach $Label; pos=$($p | ConvertTo-Json -Compress), yaw=$($car['Yaw']), distance=$distance."
            }
            if ($state.Arriving) {
                Set-Neutral 'host' "$Label-final-brake"
                if ([Math]::Abs([double]$car['Speed']) -le .02 -and $time - $state.Since -ge .7) { return $true }
                return $null
            }
            if ($distance -le .7) { $state.Arriving = $true; $state.Since = $time; Set-Neutral 'host' "$Label-brake"; return $null }
            $bearing = [Math]::Atan2($dx, $dz) * 180.0 / [Math]::PI
            if ($Reverse) { $bearing += 180.0 }
            $error = Get-AngleError $bearing ([double]$car['Yaw'])
            $steer = [Math]::Clamp($error / 30.0, -1.0, 1.0)
            if ($Reverse) { $steer = -$steer }
            $speed = [Math]::Min($MaximumSpeed, [Math]::Max(.5, $distance / 2.8))
            if ([Math]::Abs($error) -gt 35) { $speed = [Math]::Min($speed, 1.7) }
            $throttle = if ($Reverse) { -$speed / [double]$rules['VehicleReverseSpeed'] } else { $speed / [double]$rules['VehicleSpeed'] }
            Set-Input 'host' $steer $throttle ([double]$car['Yaw']) 0 $false $Label $p
            return $null
        }
    } finally {
        Set-Neutral 'host' "$Label-stop"
        $evidence.Scenario.Driving.Add(@{ Label = $Label; Reverse = $Reverse; Target = @{ x = $X; z = $Z }; From = $initialCar; Samples = $samples.ToArray() })
    }
    $final = (Get-LiveView 'host').Data['Vehicle']
    Assert-Check ((Distance-XZ $final['Position'] @{ x = $X; z = $Z }) -le 1.1 -and [Math]::Abs([double]$final['Speed']) -le .02) "Truck reached and stopped at $Label through driver axes"
    $null = Wait-View 'client' { param($view) (Distance-XZ $view['Vehicle']['Position'] $final['Position']) -le .15 -and [Math]::Abs([double]$view['Vehicle']['Speed']) -le .02 }
}
function Turn-South {
    # Low-speed turning radius is about 2.64 m; do this south of the z40 embankment.
    $samples = [Collections.Generic.List[object]]::new()
    try {
        $null = Wait-Until 'closed-loop left U-turn south of the gateway' {
            $view = (Get-LiveView 'host').Data; $car = $view['Vehicle']
            $error = Get-AngleError 180 ([double]$car['Yaw'])
            $samples.Add(@{ Seconds = $view['OnlineSeconds']; Position = $car['Position']; Yaw = $car['Yaw']; Speed = $car['Speed'] })
            if ([Math]::Abs($error) -le 4.5) { return $true }
            if ([double]$car['Position']['z'] -gt 38.2) { throw 'U-turn approached the solid embankment; stop rather than bypass collision.' }
            $steer = if ([double]$car['Yaw'] -lt 20 -or [double]$car['Yaw'] -gt 180) { -1.0 } else { 1.0 }
            $speed = if ([Math]::Abs($error) -lt 15) { .6 } else { 1.6 }
            Set-Input 'host' $steer ($speed / [double]$rules['VehicleSpeed']) ([double]$car['Yaw']) 0 $false 'left U-turn' $car['Position']
            Start-Sleep -Milliseconds 100
            return $null
        }
    } finally {
        Wait-Stopped 'U-turn'
        $evidence.Scenario.Driving.Add(@{ Label = 'left U-turn after reversing through gateway'; Samples = $samples.ToArray() })
    }
    Assert-Check ([Math]::Abs((Get-AngleError 180 ([double](Get-LiveView 'host').Data['Vehicle']['Yaw']))) -le 8) 'Truck turned south using steering and throttle'
}
function Approach-ParkedCar([string]$Role, [string]$Side) {
    $car = (Get-LiveView $Role).Data['Vehicle']; $p = $car['Position']
    # This route is used only at the northern parking area, before the return U-turn.
    $x = [double]$p['x'] + $(if ($Side -eq 'west') { -3.0 } else { 3.0 })
    Move-To $Role $x ([double]$p['z']) "parked truck $Side side"
}
function Assert-Cargo([string]$ItemId, [int]$Slot, [string]$Label) {
    foreach ($viewRole in @('host', 'client')) {
        $snapshot = Wait-Item $viewRole $ItemId 5 ''
        $item = Find-Entity $snapshot.Data 'Items' $ItemId
        Assert-Check ($item['ContainerId'] -ceq "vehicle:camp-truck:$Slot" -and $item['Quantity'] -eq 1) "$Label shared public cargo keeps ID and slot ($viewRole)"
        Assert-Projection $snapshot $playerIds[$viewRole] "$Label-$viewRole"
    }
}
function Client-DrivingPulse([string]$Label) {
    $null = Vehicle-Action 'client' 'vehicle_enter' 0
    $snapshot = Wait-View 'host' { param($view) $view['Vehicle']['Seats'][0] -ceq $playerIds.client }
    $before = $snapshot.Data['Vehicle']; $t0 = [double]$snapshot.Data['OnlineSeconds']
    try {
        Set-Input 'client' 0 .15 ([double]$before['Yaw']) 0 $false $Label $before['Position']
        $null = Wait-View 'host' { param($view) [double]$view['OnlineSeconds'] -ge $t0 + 1.1 }
    } finally { Set-Neutral 'client' "$Label-brake" }
    $after = Wait-View 'host' { param($view) [Math]::Abs([double]$view['Vehicle']['Speed']) -le .02 -and [double]$view['OnlineSeconds'] -ge $t0 + 1.8 }
    $distance = Distance-XZ $before['Position'] $after.Data['Vehicle']['Position']
    Assert-Check ($distance -ge .6 -and $distance -le 2.5) "$Label client driver has actual short road movement" "distance=$distance; PID=$($roles.client.CurrentProcess.Id)"
    $evidence.Scenario.Driving.Add(@{ Label = $Label; DriverRole = 'client'; From = $before; To = $after.Data['Vehicle']; Distance = $distance })
    Assert-Cargo $advancedItems[1] 1 "$Label-cargo"
}
function Assert-ProgressionState($State, [string]$Label) {
    Assert-Check ($State['WorldId'] -ceq $worldId -and $State['RoadUnlocked'] -and $State['RoadCoinsPaid'] -eq 400 -and $State['RoadIronPaid'] -eq 8) "$Label preserves completed public repair"
    Assert-Check (@($State['Cells']).Count -eq 224 -and @($State['Vehicles']).Count -eq 1) "$Label retains both finite pits and one truck"
    $coins = 0L; $iron = 0
    foreach ($part in $State['Contributions']) { $coins += [long]$part['Coins']; $iron += [int]$part['Iron'] }
    Assert-Check ($coins -eq 400 -and $iron -eq 8 -and @($State['Contributions']).Count -eq 2) "$Label voluntary contribution ledger balances exactly"
    foreach ($role in @('host', 'client')) {
        $player = Find-Entity $State 'Players' $playerIds[$role]
        Assert-Check ($player['Balance'] -eq $expectedBalances[$role]) "$Label $role persisted exact balance"
    }
    Assert-Check ((Find-Entity $State 'Players' $playerIds.host)['ToolLevel'] -eq 2) "$Label personal pickaxe persists"
    foreach ($id in $minedIds) {
        $cell = Find-Entity $State 'Cells' $id
        Assert-Check ($cell['Dug'] -and $cell['Version'] -eq $finalCellVersions[$id]) "$Label $id remains dug without reroll"
    }
    $car = $State['Vehicles'][0]
    Assert-Check ($car['Id'] -ceq 'camp-truck' -and @($car['Seats'] | Where-Object { -not [string]::IsNullOrEmpty($_) }).Count -eq 0 -and
        [Math]::Abs([double]$car['Speed']) -le .02 -and (Distance-XZ $car['Position'] $finalCar['Position']) -le .1) "$Label truck parked with empty seats"
    $cargo = Find-Entity $State 'Items' $advancedItems[1]
    Assert-Check ($cargo['Place'] -eq 5 -and $cargo['ContainerId'] -ceq 'vehicle:camp-truck:1' -and [string]::IsNullOrEmpty($cargo['OwnerId'])) "$Label unsold advanced cargo remains in truck"
    Assert-Check ((Find-Entity $State 'Items' $advancedItems[0])['Place'] -eq 7) "$Label transported and sold ore is not duplicated"
}

try {
    foreach ($role in @('host', 'client')) {
        $root = Assert-TestPath (Join-Path $runRoot $role)
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Write-AtomicText (Join-Path $root '.goldcamp-test-root') 'GoldCamp D isolated ordinary camp; no fixtures; never a human save.'
        $roles[$role] = @{ Root = $root; InboxLines = [Collections.Generic.List[string]]::new(); CurrentProcess = $null; LastYaw = 0.0 }
    }
    $reservation = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $reservation.Client.ExclusiveAddressUse = $true
        $reservation.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
        $evidence.Port = ([Net.IPEndPoint]$reservation.Client.LocalEndPoint).Port
    } finally { $reservation.Dispose() }
    Start-Player 'host' 'first' $true
    $null = Get-LiveView 'host'
    Start-Player 'client' 'first' $false
    $initial = @{}
    foreach ($role in @('host', 'client')) {
        $initial[$role] = Wait-View $role { param($view) @($view['Players'] | Where-Object { $_['Online'] }).Count -eq 2 -and $null -ne $view['Vehicle'] }
    }
    $playerIds = @{ host = $initial.host.Data['Self']['Id']; client = $initial.client.Data['Self']['Id'] }
    $worldId = [string]$initial.host.Data['WorldId']; $rules = $initial.host.Data['Rules']
    $expectedBalances = @{ host = 0L; client = 0L }
    Assert-Check ($playerIds.host -cne $playerIds.client -and $roles.host.CurrentProcess.Id -ne $roles.client.CurrentProcess.Id) 'Distinct player identities in two actual processes'
    Assert-Check ($initial.client.Data['WorldId'] -ceq $worldId) 'Two clients share the same ordinary host world'
    Assert-Check ($rules['UpgradePrice'] -eq 180 -and $rules['RoadPrice'] -eq 400 -and $rules['RoadIron'] -eq 8 -and
        [Math]::Abs([double]$rules['CellSize'] - 2) -lt 1e-4 -and $rules['PitWidth'] -eq 6 -and $rules['PitLength'] -eq 6 -and $rules['PitDepth'] -eq 4) 'Reviewed progression costs and route geometry are active'
    foreach ($role in @('host', 'client')) {
        $view = $initial[$role].Data
        Assert-Projection $initial[$role] $playerIds[$role] "initial-$role"
        Assert-Check ($view['Self']['Balance'] -eq 0 -and $view['Self']['ToolLevel'] -eq 1 -and -not $view['RoadUnlocked'] -and
            $view['RoadCoinsPaid'] -eq 0 -and $view['RoadIronPaid'] -eq 0 -and @($view['Cells']).Count -eq 224) "$role starts with zero money and unearned progression"
        Assert-Check (@($view['Items'] | Where-Object { $_['Id'] -notlike 'river:*' }).Count -eq 0 -and @($view['Items']).Count -eq 4) "$role has only four normal river minerals; no injected resources"
    }
    Assert-Balances 'ordinary starting balances'
    Move-To 'host' -3 -4 'host waits outside spawn lane'
    Move-To 'client' 2 -3 'river southeast approach'
    Move-To 'client' 8 -3 'river east lane'
    Move-To 'client' 13 3 'river southern bank'
    $null = Item-Action 'client' 'pickup' 'river:1'; $null = Item-Action 'client' 'pocket' 'river:1'
    Move-To 'client' 13 4.8 'river collection bank'
    foreach ($id in @('river:3', 'river:0', 'river:2')) { $null = Item-Action 'client' 'pickup' $id; $null = Item-Action 'client' 'pocket' $id }
    Move-To 'client' 8 8 'river return north of cabins'
    Move-To 'client' 2.2 2 'sell normally gathered river stone'
    Sell-Carried 'client' 'river:0'; Sell-Carried 'client' 'river:2'
    Move-To 'client' 8 8 'client public repair approach'
    Move-To 'client' 16 4 'client west of roadside trees'
    Move-To 'client' 17.8 4 'client maintenance approach'
    Move-To 'client' 17.8 -3.5 'client north of maintenance desk'
    Move-To 'client' 16.6 -3.5 'client contributes from visible front of maintenance desk'
    Contribute 'client' $false 3; Contribute 'client' $true 1
    Contribute 'client' $false 3; Contribute 'client' $true 1
    Move-To 'client' 17.8 -3.5 'client leaves desk along east side'
    Move-To 'client' 17.8 -9 'client waits beside passenger door'
    Move-To 'client' 17 -9 'client clear of driver approach'

    Move-To 'host' -3 5 'host north of camp'
    Move-To 'host' 8 18 'host starter mining hub'
    $candidates = [Collections.Generic.List[object]]::new()
    for ($x = 0; $x -lt 6; $x++) { $candidates.Add(@{ X = $x; Z = 0; Depth = 0; Side = 'South' }) }
    for ($z = 1; $z -lt 6; $z++) { $candidates.Add(@{ X = 5; Z = $z; Depth = 0; Side = 'East' }) }
    for ($x = 4; $x -ge 0; $x--) { $candidates.Add(@{ X = $x; Z = 5; Depth = 0; Side = 'North' }) }
    for ($z = 4; $z -ge 1; $z--) { $candidates.Add(@{ X = 0; Z = $z; Depth = 0; Side = 'West' }) }
    for ($d = 1; $d -le 3; $d++) { for ($x = 0; $x -lt 6; $x++) { $candidates.Add(@{ X = $x; Z = 5; Depth = $d; Side = 'North' }) } }
    for ($d = 1; $d -le 3; $d++) { for ($x = 1; $x -le 4; $x++) { $candidates.Add(@{ X = $x; Z = 0; Depth = $d; Side = 'South' }) } }
    Assert-Check ($candidates.Count -eq 50) 'Finite harvest budget is 50 fixed geometric cells'
    $evidence.Scenario['CandidateOrder'] = $candidates.ToArray()
    foreach ($candidate in $candidates) {
        if (Mining-GoalMet) { break }
        Harvest-Starter $candidate
        $carry = @(Get-Carried 'host'); $view = (Get-LiveView 'host').Data
        $potential = [long]$view['Self']['Balance']
        foreach ($item in $carry) { if ($item['Kind'] -ne 1) { $potential += [long]$item['SaleQuote'] } }
        $need = [long]$view['RoadRequiredCoins'] - [long]$view['RoadCoinsPaid']
        if ([int]$view['Self']['ToolLevel'] -lt 2) { $need += [long]$rules['UpgradePrice'] }
        if (@($carry | Where-Object { $_['Place'] -eq 1 }).Count -gt 0 -or
            @($carry | Where-Object { $_['Place'] -eq 2 }).Count -ge 4 -or
            ($potential -ge $need -and (Count-Material 'host' 1) + [int]$view['RoadIronPaid'] -ge [int]$view['RoadRequiredIron'])) {
            Service-Camp
        }
    }
    if (-not (Mining-GoalMet)) { Service-Camp }
    Assert-Check (Mining-GoalMet) 'Normal finite harvesting earned personal upgrade and complete shared repair' "starter cells mined=$($minedIds.Count); maximum=50"
    # Any carried spare minerals may remain in pockets; free hands are required to board.
    Assert-Check (@(Get-Carried 'host' | Where-Object { $_['Place'] -eq 1 }).Count -eq 0) 'Host has free hands before boarding'
    Wait-GameSeconds .8
    $null = Vehicle-Action 'host' 'vehicle_enter' 0
    $null = Vehicle-Action 'client' 'vehicle_enter' 1
    Assert-Seats $true
    $passengerStart = (Get-LiveView 'host').Data['Vehicle']['Position']
    try { Set-Input 'client' 1 1 90 0 $false 'passenger cannot drive'; Wait-GameSeconds 1.2 }
    finally { Set-Neutral 'client' 'passenger neutral' }
    Assert-Check ((Distance-XZ (Get-LiveView 'host').Data['Vehicle']['Position'] $passengerStart) -le .05) 'Passenger input cannot drive the shared truck'

    Drive-To 22 24 'northbound road approach'
    Drive-To 22 34 'aligned south of repaired gateway' $false 2.5
    Drive-To 22 44 'through repaired gateway to advanced parking' $false 2.5
    $null = Vehicle-Action 'client' 'vehicle_exit'
    $null = Vehicle-Action 'host' 'vehicle_exit'
    Assert-Seats $false
    $advancedItems = [Collections.Generic.List[string]]::new()
    for ($i = 0; $i -lt 2; $i++) {
        Move-To 'host' 17 44 'west of advanced parked truck'
        Move-To 'host' 17 47.25 'advanced southwest bank'
        Move-To 'host' (21 + 2 * $i) 47.25 'advanced south mining bank'
        $id = "advanced:$($i + 1):0:0"
        $cell = Find-Entity (Get-LiveView 'host').Data 'Cells' $id
        Assert-Check ($cell['RequiredTool'] -eq 2 -and -not $cell['Dug']) 'Advanced vein requires the personally earned pickaxe'
        $ore = Mine-Cell $id
        $null = Item-Action 'host' 'pickup' $ore
        $advancedItems.Add($ore)
        Move-To 'host' 17 47.25 'carry actual advanced ore along bank'
        Move-To 'host' 17 44 'carry to truck west approach'
        Approach-ParkedCar 'host' 'west'
        Cargo-Action 'vehicle_load' $ore $i
        Assert-Cargo $ore $i "advanced-loaded-$i"
    }
    Wait-GameSeconds .8
    $null = Vehicle-Action 'host' 'vehicle_enter' 0
    $null = Vehicle-Action 'client' 'vehicle_enter' 1
    Assert-Seats $true
    Drive-To 22 34.5 'reverse straight south through gateway' $true 1.8
    Turn-South
    Drive-To 20 26 'southbound open road'
    Drive-To 20 8 'return toward permanent camp'
    Drive-To 20 -7 'return truck to camp parking' $false 2.5
    Assert-Cargo $advancedItems[0] 0 'returned-first-cargo'
    Assert-Cargo $advancedItems[1] 1 'returned-reserved-cargo'
    $null = Vehicle-Action 'client' 'vehicle_exit'
    $null = Vehicle-Action 'host' 'vehicle_exit'
    Assert-Seats $false
    Cargo-Action 'vehicle_unload' $advancedItems[0]
    $null = Wait-Item 'host' $advancedItems[0] 1 $playerIds.host
    $position = (Get-Miner (Get-LiveView 'host').Data)['Position']
    if ([double]$position['x'] -gt 20) {
        Move-To 'host' 23 $position['z'] 'unloaded ore east of truck'
        Move-To 'host' 23 -3 'around front of parked truck'
        Move-To 'host' 17.8 -3 'cross north of truck'
    } else { Move-To 'host' 17.8 $position['z'] 'unloaded ore west of truck' }
    Move-To 'host' 17.8 4 'carry through maintenance side lane'
    Move-To 'host' 16 4 'carry west of roadside trees'
    Move-To 'host' 8 8 'carry advanced cargo into camp'
    Move-To 'host' 2.2 2 'sell advanced ore after truck transport'
    Sell-Carried 'host' $advancedItems[0]
    Assert-Cargo $advancedItems[1] 1 'one-advanced-cargo-stays-for-save'
    # Reconnect the actual client process while the host remains online. The old
    # process has already sent thousands of frames; its sequence must not poison
    # the newly connected driver's reset input sequence.
    Client-DrivingPulse 'client drives before process reconnect'
    $driverBeforeDisconnect = (Get-LiveView 'host').Data['Vehicle']
    $oldClientPid = $roles.client.CurrentProcess.Id
    Stop-Player 'client'
    Wait-GameSeconds 1.2
    $disconnected = (Get-LiveView 'host').Data['Vehicle']
    Assert-Check ([string]::IsNullOrEmpty($disconnected['Seats'][0]) -and [Math]::Abs([double]$disconnected['Speed']) -le .02 -and
        (Distance-XZ $disconnected['Position'] $driverBeforeDisconnect['Position']) -le .1) 'Driver disconnect clears seat and parks truck without teleporting cargo'
    $cargo = Find-Entity (Get-LiveView 'host').Data 'Items' $advancedItems[1]
    Assert-Check ($cargo['Place'] -eq 5 -and $cargo['ContainerId'] -ceq 'vehicle:camp-truck:1') 'Driver disconnect keeps the original cargo in its public slot'
    Archive-ControlFiles 'client' 'before-driver-reconnect'
    Start-Player 'client' 'driver-reconnect' $false
    $rejoined = Wait-View 'client' { param($view) @($view['Players'] | Where-Object { $_['Online'] }).Count -eq 2 }
    Assert-Check ($rejoined.Data['Self']['Id'] -ceq $playerIds.client -and $roles.client.CurrentProcess.Id -ne $oldClientPid) 'New client process rejoins with the same persistent identity'
    Client-DrivingPulse 'client drives after process reconnect'
    $null = Vehicle-Action 'client' 'vehicle_exit'
    Assert-Seats $false
    Assert-Balances 'end of earned progression route'
    $final = (Get-LiveView 'host').Data
    $finalCar = $final['Vehicle']; $finalCellVersions = @{}
    foreach ($id in $minedIds) { $finalCellVersions[$id] = (Find-Entity $final 'Cells' $id)['Version'] }
    $evidence.Scenario['ExpectedBalances'] = $expectedBalances.Clone()
    $evidence.Scenario['AdvancedCargoIds'] = $advancedItems.ToArray()
    $evidence.Scenario['FinalVehicle'] = $finalCar
    $evidence.Scenario['FinalCellVersions'] = $finalCellVersions
    Stop-Player 'client'; Wait-GameSeconds 1.2; Stop-Player 'host'
    $saved = Read-World $worldId
    Assert-ProgressionState $saved 'first clean save'

    foreach ($role in @('host', 'client')) { Archive-ControlFiles $role }
    Start-Player 'host' 'reopen' $false
    $null = Get-LiveView 'host'
    Start-Player 'client' 'reopen' $false
    foreach ($role in @('host', 'client')) {
        $snapshot = Wait-View $role { param($view) @($view['Players'] | Where-Object { $_['Online'] }).Count -eq 2 }
        Assert-Projection $snapshot $playerIds[$role] "reopened-$role"
        Assert-Check ($snapshot.Data['WorldId'] -ceq $worldId -and $snapshot.Data['RoadUnlocked'] -and $snapshot.Data['RoadCoinsPaid'] -eq 400 -and $snapshot.Data['RoadIronPaid'] -eq 8) "$role resumes exact existing unlocked camp"
        foreach ($id in $minedIds) {
            $cell = Find-Entity $snapshot.Data 'Cells' $id
            Assert-Check ($cell['Dug'] -and $cell['Version'] -eq $finalCellVersions[$id]) "$role reopens mined $id unchanged"
        }
    }
    Assert-Balances 'reopening never recharges upgrade or repair'
    Assert-Cargo $advancedItems[1] 1 'reopened-truck-cargo'
    foreach ($receipt in $replays) {
        Send-Command $receipt.Role $receipt.Command
        $replay = (Wait-Result $receipt.Role $receipt.Command.RequestId).Entries[0]
        Assert-ResultSame $replay $receipt.Result "replay $($receipt.Command.Action) after restart"
    }
    Assert-Balances 'replayed earnings and spending stay exactly once'
    Stop-Player 'client'; Wait-GameSeconds .7; Stop-Player 'host'
    $savedAgain = Read-World $worldId
    Assert-ProgressionState $savedAgain 'second clean save'
    foreach ($entry in $processes) {
        $badLines = @(Select-String -LiteralPath $entry.LogPath -Pattern 'IOException|NullReferenceException|MissingReferenceException|InvalidOperationException|IndexOutOfRangeException|GOLDCAMP.*(FAILED|ERROR)' -ErrorAction Stop)
        Assert-Check ($badLines.Count -eq 0) "$($entry.Role)/$($entry.Phase) has no matching runtime exceptions" 'Only count and log path retained; log contents are not copied.'
    }
    $evidence.Success = $true
} catch {
    $evidence.Error = $_.Exception.Message
    $evidence.Scenario['FailureLine'] = $_.InvocationInfo.ScriptLineNumber
} finally {
    # Stop only handles created by this script. Never identify processes by name.
    foreach ($entry in $processes) {
        if (-not $entry.Process.HasExited) {
            $entry.ExpectedRunning = $false
            try { Set-Neutral $entry.Role 'failure-neutral'; Send-Command $entry.Role (New-Command 'd-cleanup-shutdown' 'shutdown') } catch { }
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
            ExitCode = $exitCode; ForcedTermination = $entry.Forced; LogPath = $entry.LogPath; TestRoot = $entry.Root
        }
    }
    $evidence.FinishedUtc = [DateTime]::UtcNow.ToString('O')
    $evidencePath = Join-Path $runRoot 'evidence.json'
    Write-AtomicText $evidencePath (ConvertTo-Json -InputObject $evidence -Depth 40)
}
[pscustomobject]@{ Success = $evidence.Success; EvidencePath = $evidencePath; RunRoot = $runRoot; Checks = $checks.Count; Port = $evidence.Port }
if (-not $evidence.Success) { throw "Demo progression validation failed. Evidence: $evidencePath" }
