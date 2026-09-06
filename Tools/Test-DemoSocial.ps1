#requires -Version 7.0
<#
.SYNOPSIS
Exercises checkpoint C using two real FishNet players and networked movement axes.
.DESCRIPTION
Creates fresh, marked test roots under .local/TestRuns; never accepts a save path.
CampTestDriver consumes inbox.jsonl. commands.jsonl is an identical audit copy.
Exercises mouth, performances, stun, pocket contention, raw processing, wagers and save reopening.
Requires the Development-only --gc-test-social fixture. It does not prove human progression.
This is automated loopback validation, not human input, LAN, Steam, or voice testing.
Use -CrashOnWagerLock for the marked Development host's committed-lock hold,
an intentional host process termination, and recovery from the same real save.
Use -ValidateInputMathOnly to check movement calculations without files or player processes.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 300)] [int]$TimeoutSeconds = 90,
    [switch]$WithGraphics,
    [switch]$CrashOnWagerLock,
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
    Suite = 'C-social-input-two-process-loopback'
    Mode = $(if ($WithGraphics) { 'Automated player processes with graphics' } else { 'Automated -batchmode -nographics player processes' })
    NotValidated = @('human input', 'visual readability', 'human progression from mining', 'two thieves plus one victim', 'locked-state crash timing', 'LAN devices', 'cross-network play', 'Steam', 'voice', 'four humans')
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
    if ($suiteTimer.Elapsed.TotalMinutes -gt 20) { throw 'Social suite exceeded its 20-minute hard deadline.' }
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
        $arguments.Add('--gc-host')
        if ($NewWorld) { $arguments.Add('--gc-new'); $arguments.Add('--gc-seed'); $arguments.Add([string]$socialSeed); $arguments.Add('--gc-test-social') }
        if ($NewWorld -and $CrashOnWagerLock) { $arguments.Add('--gc-test-hold-wager-lock') }
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
        LogPath = $logPath; ExpectedRunning = $true; Forced = $false; IntentionalCrash = $false
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
    Send-Command $Role (New-Command "c-shutdown-$Role-$($entry.Phase)" 'shutdown')
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
    foreach ($file in @('view.json', 'results.jsonl', 'inbox.jsonl', 'commands.jsonl', 'process.json', 'input.json', 'inputs.jsonl', 'fault-lock.json')) {
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


$socialSeed = 6
$commandNumber = 0
$evidence.Scenario['Seed'] = $socialSeed
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
    $command = New-Command ("c-$Label-" + $script:commandNumber) $Action $Target $Version
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
function Player-Action([string]$Role, [string]$Action, [string]$TargetRole) {
    $view = (Get-LiveView $Role).Data
    $target = (Find-Entity $view 'Players' $playerIds[$TargetRole])['Position']
    $direction = Aim-At (Get-Miner $view)['Position'] ([double]$target['x']) ([double]$target['y'] + 1.0) ([double]$target['z'])
    return New-Action $Action $Action $playerIds[$TargetRole] 0 $direction
}
function Wait-Action([string]$Viewer, [string]$ActorRole, [string]$Action) {
    return Wait-View $Viewer { param($view)
        $actor = Find-Entity $view 'Players' $playerIds[$ActorRole]
        $actor['Action'] -ceq $Action -and [double]$actor['ActionUntil'] -gt [double]$view['OnlineSeconds'] }
}
function Assert-MouthHidden([bool]$Full, [string]$Label) {
    $own = Wait-View 'host' { param($view)
        (Get-Miner $view)['CheeksFull'] -eq $Full -and
        ((@($view['Items'] | Where-Object { $_['Id'] -ceq 'c-mouth-gold' -and $_['Place'] -eq 3 }).Count -eq 1) -eq $Full) }
    $other = Wait-View 'client' { param($view)
        (Find-Entity $view 'Players' $playerIds.host)['CheeksFull'] -eq $Full -and
        @($view['Items'] | Where-Object { $_['Place'] -eq 3 -and $_['OwnerId'] -ceq $playerIds.host }).Count -eq 0 }
    Assert-Check ((Find-Entity $other.Data 'Players' $playerIds.host)['CheeksFull'] -eq $Full) "$Label visible cheeks, hidden mouth contents"
    Assert-Projection $own $playerIds.host "$Label-host"
    Assert-Projection $other $playerIds.client "$Label-client"
}
function Offer([string]$Role, [string]$OtherRole, [string]$RawId, [long]$Stake) {
    $view = (Get-LiveView $Role).Data
    $version = if ($null -eq $view['Wager'] -or [string]::IsNullOrEmpty($view['Wager']['Id'])) { 0 } else { [int]$view['Wager']['Version'] }
    $command = New-Action 'offer' 'wager_offer' $RawId $version
    $command.OtherPlayerId = $playerIds[$OtherRole]
    $command.Amount = $Stake
    return Invoke-Action $Role $command
}
function Wager-Command([string]$Role, [string]$Action) {
    $wager = (Get-LiveView $Role).Data['Wager']
    if ($null -eq $wager -or [string]::IsNullOrEmpty($wager['Id'])) { throw 'No current wager in the client projection.' }
    return New-Action $Action $Action $wager['Id'] ([int]$wager['Version'])
}
function Wait-WagerVersion([long]$Version) {
    foreach ($role in @('host', 'client')) {
        $null = Wait-View $role { param($view) $null -ne $view['Wager'] -and [long]$view['Wager']['Version'] -ge $Version }
    }
}
function Assert-Balances([string]$Label) {
    $total = 0L
    foreach ($role in @('host', 'client')) {
        $snapshot = Wait-View $role { param($view) [long]$view['Self']['Balance'] -eq $expectedBalances[$role] }
        $total += [long]$snapshot.Data['Self']['Balance']
        Assert-Projection $snapshot $playerIds[$role] "$Label-$role"
    }
    Assert-Check ($total -eq $expectedBalances.host + $expectedBalances.client) "$Label both account balances match conserved result"
}
function Position-Distance($First, $Second) {
    $dx = [double]$First['x'] - [double]$Second['x']
    $dy = [double]$First['y'] - [double]$Second['y']
    $dz = [double]$First['z'] - [double]$Second['z']
    return [Math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
}
function Assert-OfflineBag($View, [string]$Label, $Expected = $null) {
    $first = Find-Entity $View 'Items' $departureSnapshot.ItemIds[0]
    $container = [string]$first['ContainerId']
    Assert-Check ($container.StartsWith('offline-', [StringComparison]::Ordinal)) "$Label uses an offline cargo bag"
    foreach ($itemId in $departureSnapshot.ItemIds) {
        $item = Find-Entity $View 'Items' $itemId
        $detail = ConvertTo-Json -Compress -Depth 5 -InputObject @{ ItemId = $itemId; Departure = $departureSnapshot.Position; BagPosition = $item['Position'] }
        Assert-Check ($item['Place'] -eq 0 -and [string]::IsNullOrEmpty($item['OwnerId']) -and
            $item['ContainerId'] -ceq $container) "$Label $itemId belongs to the same ground bag" $detail
        Assert-Check ((Position-Distance $item['Position'] $first['Position']) -lt .001 -and
            (Position-Distance $item['Position'] $departureSnapshot.Position) -le 1.5) "$Label $itemId shares one nearby physical position" $detail
    }
    Assert-Check (@($View['Items'] | Where-Object { $_['ContainerId'] -ceq $container }).Count -eq $departureSnapshot.ItemIds.Count) "$Label bag retains exactly the departing small cargo"
    if ($null -ne $Expected) {
        Assert-Check ($container -ceq $Expected.ContainerId -and
            (Position-Distance $first['Position'] $Expected.Position) -le .05) "$Label preserves the same bag and settled position" (
                ConvertTo-Json -Compress -Depth 5 -InputObject @{ Before = $Expected.Position; After = $first['Position'] })
    }
    return @{ ContainerId = $container; Position = $first['Position'] }
}
function Assert-SocialState($State, [string]$Label) {
    Assert-Check ($State['WorldId'] -ceq $worldId -and @($State['Players']).Count -eq 2) "$Label preserves world and two identities"
    Assert-Check (@($State['Wagers']).Count -eq 1) "$Label contains one persisted wager record"
    $wager = $State['Wagers'][0]
    Assert-Check ($null -ne $wager -and $wager['Id'] -ceq $wagerId -and $wager['Phase'] -eq 2 -and
        $wager['WinnerId'] -ceq $playerIds.client -and $wager['Stake'] -eq $stake) "$Label has the same settled wager"
    Assert-Check (@($State['Items'] | Where-Object { $_['Place'] -eq 6 }).Count -eq 0) "$Label has no residual escrow"
    $unique = @($State['Items'] | ForEach-Object { $_['Id'] } | Sort-Object -Unique)
    Assert-Check ($unique.Count -eq @($State['Items']).Count) "$Label has unique item IDs"
    foreach ($role in @('host', 'client')) {
        $owner = Find-Entity $State 'Players' $playerIds[$role]
        Assert-Check ($owner['Balance'] -eq $expectedBalances[$role]) "$Label $role balance persisted"
        $stone = Find-Entity $State 'Items' $rawIds[$role]
        Assert-Check ($stone['OwnerId'] -ceq $playerIds[$role] -and $stone['Place'] -eq 4 -and
            $stone['ContainerId'] -ceq ('cabinet:' + $playerIds[$role]) -and $stone['RevealStage'] -eq 2 -and
            $stone['Quantity'] -eq 1 -and $stone['HiddenValue'] -eq $fixtureValues[$role] -and
            $stone['CoreKind'] -ceq 'gold' -and $stone['RawWindowBand'] -eq $windowBands[$role] -and
            $stone['Version'] -eq $settledVersions[$role]) "$Label $role retains the same fully opened stone in its cabinet"
        $sold = Find-Entity $State 'Items' $fundIds[$role]
        Assert-Check ($sold['Place'] -eq 7 -and $sold['Quantity'] -eq 10) "$Label $role funding gold remains sold"
    }
    foreach ($replay in $replays) {
        $receipt = @($State['Receipts'] | Where-Object { $_['Key'] -ceq ($playerIds[$replay.Role] + ':' + $replay.Command.RequestId) })
        Assert-Check ($receipt.Count -eq 1 -and $receipt[0]['Result']['Success']) "$Label has exactly one $($replay.Command.Action) receipt"
    }
    $null = Assert-OfflineBag $State $Label $offlineBag
}

function Recover-FromLockedWager($LockCommand, $LockResult) {
    $fault = Wait-Until 'host marker for the normally committed locked wager' {
        $marker = Read-JsonSnapshot (Join-Path $roles.host.Root 'fault-lock.json')
        if ($null -eq $marker) { return $null }
        return $marker.Data
    }
    Assert-Check ($fault['WorldId'] -ceq $worldId -and $fault['WagerId'] -ceq $LockCommand.TargetId -and
        $fault['RequestId'] -ceq $LockCommand.RequestId -and [long]$fault['Revision'] -eq [long]$LockResult['Revision']) 'Fault marker identifies the exact successful lock transaction'
    $lockedId = [string]$fault['WagerId']
    $lockedItemVersions = @{}; $carriedBefore = @{}; $lockedTotal = 0L
    $lockedWagerVersion = -1
    foreach ($role in @('host', 'client')) {
        $snapshot = Wait-View $role { param($view) $null -ne $view['Wager'] -and
            $view['Wager']['Id'] -ceq $lockedId -and $view['Wager']['Phase'] -eq 1 -and
            [long]$view['Revision'] -ge [long]$fault['Revision'] }
        $wager = $snapshot.Data['Wager']
        Assert-Check ($wager['ConfirmedA'] -and $wager['ConfirmedB'] -and $wager['Stake'] -eq $stake -and
            $wager['ValueA'] -eq 0 -and $wager['ValueB'] -eq 0 -and [string]::IsNullOrEmpty($wager['WinnerId'])) "$role sees committed lock without the unrevealed outcome"
        Assert-Check ($snapshot.Data['Self']['Balance'] -eq $expectedBalances[$role] - $stake) "$role paid exactly one locked stake before the crash"
        $lockedTotal += [long]$snapshot.Data['Self']['Balance']
        $escrow = @($snapshot.Data['Items'] | Where-Object { $_['Place'] -eq 6 })
        Assert-Check ($escrow.Count -eq 2) "$role sees exactly two escrow stones before the crash"
        foreach ($ownerRole in @('host', 'client')) {
            $stone = Find-Entity $snapshot.Data 'Items' $rawIds[$ownerRole]
            Assert-Check ($stone['Place'] -eq 6 -and $stone['OwnerId'] -ceq $playerIds[$ownerRole] -and
                $stone['ContainerId'] -ceq ('wager:' + $lockedId) -and $stone['Quantity'] -eq 1 -and
                $stone['RevealStage'] -eq 1 -and $stone['WindowBand'] -eq $windowBands[$ownerRole] -and
                [string]::IsNullOrEmpty($stone['PublicCore'])) "$role receives the original $ownerRole stone in escrow"
            if ($ownerRole -cne $role) {
                Assert-Check ([string]::IsNullOrEmpty($stone['OwnClue'])) "$role cannot see the other escrow owner's private clue"
            }
            $lockedItemVersions[$rawIds[$ownerRole]] = [int]$stone['Version']
        }
        $carriedBefore[$role] = @($snapshot.Data['Items'] | Where-Object {
            $_['OwnerId'] -ceq $playerIds[$role] -and $_['Place'] -in @(1, 2, 3) } | ForEach-Object {
                @{ Id = $_['Id']; Place = $_['Place']; Quantity = $_['Quantity']; Version = $_['Version'] } })
        $lockedWagerVersion = [int]$wager['Version']
        Assert-Projection $snapshot $playerIds[$role] "locked-before-crash-$role"
    }
    Assert-Check ($lockedTotal + 2 * $stake -eq $expectedBalances.host + $expectedBalances.client) 'Two debited balances plus escrow pot conserve the pre-lock money'
    $hostEntry = $roles.host.CurrentProcess
    Assert-Check ($hostEntry.Role -ceq 'host' -and -not $hostEntry.Process.HasExited -and
        $hostEntry.Root -ceq $roles.host.Root) 'Fault targets only the currently owned isolated host process'
    $evidence.Scenario['WagerLockFault'] = [ordered]@{
        Status = 'confirmed_locked'; WorldId = $worldId; WagerId = $lockedId; Revision = $fault['Revision']
        RequestId = $LockCommand.RequestId; HostPid = $hostEntry.Id; LockedItemVersions = $lockedItemVersions
        LockReceipt = $LockResult; IntentionalCrashUtc = [DateTime]::UtcNow.ToString('O')
    }
    $hostEntry.ExpectedRunning = $false; $hostEntry.IntentionalCrash = $true
    $hostEntry.Process.Kill()
    if (-not $hostEntry.Process.WaitForExit(10000)) { throw 'The owned locked-wager host did not terminate within the fault-injection deadline.' }
    Assert-Check ($hostEntry.Process.HasExited -and -not $hostEntry.Forced) 'Intentional locked-wager host crash is separate from forced cleanup'
    Stop-Player 'client'

    # Read the unedited, checksummed main file only after the host has terminated.
    # The hook pauses ticking and autosave; it never writes fabricated wager data.
    $saved = Read-World $worldId
    Assert-Check ([long]$saved['Revision'] -eq [long]$fault['Revision'] -and @($saved['Wagers']).Count -eq 1) 'The on-disk commit is the exact acknowledged locked revision'
    $savedWager = $saved['Wagers'][0]
    Assert-Check ($savedWager['Id'] -ceq $lockedId -and $savedWager['Phase'] -eq 1 -and
        $savedWager['Version'] -eq $lockedWagerVersion -and @($saved['Items'] | Where-Object { $_['Place'] -eq 6 }).Count -eq 2) 'Actual save still contains the locked wager and two escrow items'
    foreach ($role in @('host', 'client')) {
        $player = Find-Entity $saved 'Players' $playerIds[$role]
        $stone = Find-Entity $saved 'Items' $rawIds[$role]
        Assert-Check ($player['Balance'] -eq $expectedBalances[$role] - $stake -and $stone['Place'] -eq 6 -and
            $stone['OwnerId'] -ceq $playerIds[$role] -and $stone['ContainerId'] -ceq ('wager:' + $lockedId) -and
            $stone['Version'] -eq $lockedItemVersions[$rawIds[$role]] -and $stone['HiddenValue'] -eq $fixtureValues[$role]) "$role original stake and stone were durably escrowed before host termination"
    }
    $receipt = @($saved['Receipts'] | Where-Object { $_['Key'] -ceq ($playerIds.client + ':' + $LockCommand.RequestId) })
    Assert-Check ($receipt.Count -eq 1) 'The exact lock request has one durable on-disk receipt'
    Assert-ResultSame $receipt[0]['Result'] $LockResult 'On-disk lock receipt matches the pre-crash acknowledgement'
    foreach ($role in @('host', 'client')) { Archive-ControlFiles $role 'before-lock-recovery' }
    Start-Player 'host' 'wager-recovery' $false
    $null = Get-LiveView 'host'
    Start-Player 'client' 'wager-recovery' $false
    foreach ($role in @('host', 'client')) {
        $snapshot = Wait-View $role { param($view) $view['WorldId'] -ceq $worldId -and
            $view['Self']['Id'] -ceq $playerIds[$role] -and @($view['Players'] | Where-Object { $_['Online'] }).Count -eq 2 -and
            $null -ne $view['Wager'] -and $view['Wager']['Id'] -ceq $lockedId -and $view['Wager']['Phase'] -eq 2 }
        Assert-Check ($snapshot.Data['Wager']['Version'] -eq $lockedWagerVersion + 1 -and
            @($snapshot.Data['Items'] | Where-Object { $_['Place'] -eq 6 }).Count -eq 0) "$role sees exactly one recovered settlement and no escrow residue"
        $stone = Find-Entity $snapshot.Data 'Items' $rawIds[$role]
        Assert-Check ($stone['Place'] -eq 4 -and $stone['OwnerId'] -ceq $playerIds[$role] -and
            $stone['ContainerId'] -ceq ('cabinet:' + $playerIds[$role]) -and $stone['Version'] -eq $lockedItemVersions[$rawIds[$role]] + 1) "$role recovery returned the same stone to its original owner's cabinet once"
        foreach ($held in $carriedBefore[$role]) {
            $item = Find-Entity $snapshot.Data 'Items' $held.Id
            Assert-Check ($item['OwnerId'] -ceq $playerIds[$role] -and $item['Place'] -eq $held.Place -and
                $item['Quantity'] -eq $held.Quantity -and $item['Version'] -eq $held.Version) "$role unrelated carried item survived the committed save unchanged"
        }
        Assert-Projection $snapshot $playerIds[$role] "locked-recovery-settled-$role"
    }
    Send-Command 'client' $LockCommand
    Assert-ResultSame (Wait-Result 'client' $LockCommand.RequestId).Entries[0] $LockResult 'Recovered final confirmation replays its original receipt'
    foreach ($role in @('host', 'client')) {
        $expected = if ($role -eq 'host') { $expectedBalances.host - $stake } else { $expectedBalances.client + $stake }
        $snapshot = Wait-View $role { param($view) [long]$view['Self']['Balance'] -eq $expected }
        Assert-Check ($snapshot.Data['Wager']['Version'] -eq $lockedWagerVersion + 1) "$role confirmation replay does not settle or charge a second time"
    }
    $evidence.Scenario.WagerLockFault.Status = 'recovered_once_from_unedited_locked_commit'
    $evidence.NotValidated = @($evidence.NotValidated | Where-Object { $_ -ne 'locked-state crash timing' }) + @('uncontrolled wager crash timing')
}

try {
    foreach ($role in @('host', 'client')) {
        $root = Assert-TestPath (Join-Path $runRoot $role)
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Write-AtomicText (Join-Path $root '.goldcamp-test-root') 'GoldCamp C isolated automated fixture; never a human save.'
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
    $fixtureIds = @('c-gold-a', 'c-gold-b', 'c-raw-a', 'c-raw-b', 'c-mouth-gold', 'c-rob-stone', 'c-rob-stone-2')
    $initial = @{}
    foreach ($role in @('host', 'client')) {
        $initial[$role] = Wait-View $role { param($view)
            @($view['Players'] | Where-Object { $_['Online'] }).Count -eq 2 -and
            @($view['Items'] | Where-Object { $_['Id'] -in $fixtureIds }).Count -eq $fixtureIds.Count }
    }
    $playerIds = @{ host = $initial.host.Data['Self']['Id']; client = $initial.client.Data['Self']['Id'] }
    $worldId = [string]$initial.host.Data['WorldId']
    $rules = $initial.host.Data['Rules']
    $initialBalances = @{ host = [long]$initial.host.Data['Self']['Balance']; client = [long]$initial.client.Data['Self']['Balance'] }
    $expectedBalances = @{ host = $initialBalances.host; client = $initialBalances.client }
    $rawIds = @{ host = 'c-raw-a'; client = 'c-raw-b' }
    $fundIds = @{ host = 'c-gold-a'; client = 'c-gold-b' }
    $fixtureValues = @{ host = 120; client = 240 }
    $clues = @{}; $windowBands = @{}; $settledVersions = @{}
    $replays = [Collections.Generic.List[object]]::new()
    Assert-Check ($playerIds.host -cne $playerIds.client -and $roles.host.CurrentProcess.Id -ne $roles.client.CurrentProcess.Id) 'Distinct identities in two real OS processes'
    Assert-Check ($initial.client.Data['WorldId'] -ceq $worldId) 'Both processes share one host world'
    Assert-Check ($rules['Reach'] -eq 3 -and $rules['RobSeconds'] -eq 1 -and [Math]::Abs([double]$rules['RobReach'] - 1.8) -lt 1e-4 -and
        $rules['StunSeconds'] -eq 2 -and $rules['ProtectionSeconds'] -eq 4) 'Reviewed default distances and social timing are active'
    foreach ($role in @('host', 'client')) {
        Assert-Projection $initial[$role] $playerIds[$role] "initial-$role"
        $gold = Find-Entity $initial[$role].Data 'Items' $fundIds[$role]
        $raw = Find-Entity $initial[$role].Data 'Items' $rawIds[$role]
        Assert-Check ($gold['Kind'] -eq 2 -and $gold['Quantity'] -eq 10 -and $gold['SaleQuote'] -eq 550 -and
            $raw['Kind'] -eq 4 -and $raw['Quantity'] -eq 1 -and $raw['RevealStage'] -eq 0 -and
            [string]::IsNullOrEmpty($raw['PublicCore'])) "$role sees only the reviewed new-camp fixture"
    }

    # Stay in the open spawn lane. No command or file changes authoritative coordinates.
    Move-To 'host' -3 -4 'mouth and social lane'
    Move-To 'client' -1.7 -4 'nearby observer and attacker'
    $null = Item-Action 'host' 'pickup' 'c-mouth-gold'
    $null = Item-Action 'host' 'mouth' 'c-mouth-gold'
    Assert-MouthHidden $true 'mouth-occupied'
    foreach ($action in @('smoke', 'drink')) {
        $failure = Invoke-Action 'host' (New-Action 'mouth-conflict' $action) $false
        Assert-Check ($failure['Code'] -eq 'mouth_full') "$action refuses occupied mouth without consuming the gold"
    }
    $null = Item-Action 'host' 'spit' 'c-mouth-gold' $true ([ordered]@{ x = -1; y = 0; z = 0 })
    Assert-MouthHidden $false 'mouth-spit'
    $null = Wait-Item 'host' 'c-mouth-gold' 0 ''
    foreach ($action in @('smoke', 'drink')) {
        $null = Invoke-Action 'host' (New-Action 'performance' $action)
        $view = Wait-Action 'client' 'host' $action
        Assert-Projection $view $playerIds.client "visible-$action"
        Assert-Check ($true) "Other process receives active $action performance"
        $null = Invoke-Action 'host' (New-Action 'stop-performance' 'cancel_action')
        $null = Wait-View 'client' { param($data) [string]::IsNullOrEmpty((Find-Entity $data 'Players' $playerIds.host)['Action']) }
    }
    $null = Item-Action 'host' 'pickup' 'c-mouth-gold'
    $null = Item-Action 'host' 'mouth' 'c-mouth-gold'
    $null = Item-Action 'host' 'pickup' 'c-rob-stone'
    $null = Item-Action 'host' 'pocket' 'c-rob-stone'
    Assert-MouthHidden $true 'mouth-before-hit'
    $victimBeforeHit = (Get-Miner (Get-LiveView 'host').Data)['Position']
    $hit = Invoke-Action 'client' (Player-Action 'client' 'attack' 'host')
    $stunned = Wait-View 'client' { param($view)
        $victim = Find-Entity $view 'Players' $playerIds.host
        [double]$victim['StunnedUntil'] -gt [double]$view['OnlineSeconds'] -and -not $victim['CheeksFull'] }
    $stunUntil = [double](Find-Entity $stunned.Data 'Players' $playerIds.host)['StunnedUntil']
    $dropped = Wait-Item 'client' 'c-mouth-gold' 0 ''
    $dropPosition = (Find-Entity $dropped.Data 'Items' 'c-mouth-gold')['Position']
    Assert-Check ((Position-Distance $dropPosition $victimBeforeHit) -lt [double]$rules['Reach']) 'Hit ejects mouth gold within original victim reach' (
        ConvertTo-Json -Compress -InputObject @{ Victim = $victimBeforeHit; Gold = $dropPosition })
    $null = Wait-Item 'host' 'c-rob-stone' 2 $playerIds.host
    Assert-Check ($true) 'Effective hit ejects mouth gold and preserves pocket stone'
    Wait-GameSeconds ([double]$rules['MineInterval'] + .15)
    $repeatHit = Invoke-Action 'client' (Player-Action 'client' 'attack' 'host') $false
    Assert-Check ($repeatHit['Code'] -eq 'protected') 'Recovery protection rejects a fresh attack after swing cooldown'
    $afterHit = Find-Entity (Get-LiveView 'client').Data 'Players' $playerIds.host
    Assert-Check ([double]$afterHit['StunnedUntil'] -eq $stunUntil) 'Rejected attack does not extend the victim stun'
    $null = Wait-View 'host' { param($view) [double]$view['OnlineSeconds'] -ge $stunUntil + [double]$rules['ProtectionSeconds'] + .2 }
    $restingGold = (Find-Entity (Get-LiveView 'host').Data 'Items' 'c-mouth-gold')['Position']
    Assert-Check ((Position-Distance $restingGold $victimBeforeHit) -lt [double]$rules['Reach']) 'Mouth gold stays near victim through recovery before pickup' (
        ConvertTo-Json -Compress -InputObject @{ Victim = $victimBeforeHit; Gold = $restingGold })
    $null = Item-Action 'host' 'pickup' 'c-mouth-gold'
    $null = Item-Action 'host' 'pocket' 'c-mouth-gold'

    # The victim competes for the exact first pocket item during the one-second action.
    # It must cancel, never silently switch to the other pocket slot.
    $victimItem = Find-Entity (Get-LiveView 'host').Data 'Items' 'c-mouth-gold'
    $robConflict = Player-Action 'client' 'rob' 'host'
    Send-Command 'client' $robConflict
    $startedRob = (Wait-Result 'client' $robConflict.RequestId).Entries[0]
    Assert-Check ($startedRob['Success']) 'One-second pocket search started before victim contention'
    $unpack = New-Action 'victim-contends-pocket' 'unpocket' 'c-mouth-gold' ([int]$victimItem['Version'])
    $null = Invoke-Action 'host' $unpack
    Wait-GameSeconds ([double]$rules['RobSeconds'] + .65)
    $null = Wait-Item 'host' 'c-mouth-gold' 1 $playerIds.host
    $null = Wait-Item 'host' 'c-rob-stone' 2 $playerIds.host
    $clientView = (Get-LiveView 'client').Data
    Assert-Check ([string]::IsNullOrEmpty((Get-Miner $clientView)['Action']) -and
        @($clientView['Items'] | Where-Object { $_['Place'] -eq 1 -and $_['OwnerId'] -ceq $playerIds.client }).Count -eq 0) 'Competing victim take cancels search without substituting another pocket item'
    $null = Item-Action 'host' 'pocket' 'c-mouth-gold'
    $rob = Player-Action 'client' 'rob' 'host'
    $robResult = Invoke-Action 'client' $rob
    $performing = Wait-Action 'host' 'client' 'rob'
    $robUntil = [double](Find-Entity $performing.Data 'Players' $playerIds.client)['ActionUntil']
    $taken = Wait-Item 'client' 'c-mouth-gold' 1 $playerIds.client
    Assert-Check ([double]$taken.Data['OnlineSeconds'] -ge $robUntil) 'Pocket transfer occurs only after the search deadline'
    $remaining = Wait-Item 'host' 'c-rob-stone' 2 $playerIds.host
    Assert-Check (@($remaining.Data['Items'] | Where-Object { $_['Id'] -ceq 'c-mouth-gold' -and $_['OwnerId'] -ceq $playerIds.host }).Count -eq 0) 'Exactly the selected item changes owner and the other pocket remains'
    Send-Command 'client' $rob
    Assert-ResultSame (Wait-Result 'client' $rob.RequestId 2).Entries[1] $robResult 'Repeated search request cannot take another pocket'
    $null = Item-Action 'client' 'pocket' 'c-mouth-gold'
    # The existing second fixture stone is already within normal pickup reach here.
    # Keep two small cargo IDs for the later real disconnect bag check.
    $null = Item-Action 'client' 'pickup' 'c-rob-stone-2'
    $null = Item-Action 'client' 'pocket' 'c-rob-stone-2'
    Assert-Balances 'pvp-no-wallet-change'

    # Fund both accounts by physically collecting and selling the fixed material piles.
    $null = Item-Action 'host' 'pickup' $fundIds.host
    Move-To 'host' -1.8 -1.8 'sale west side'
    Move-To 'client' 2 -3.8 'east funding pickup'
    $null = Item-Action 'client' 'pickup' $fundIds.client
    Move-To 'client' 1.8 -1.8 'sale east side'
    foreach ($role in @('host', 'client')) {
        $gold = Find-Entity (Get-LiveView $role).Data 'Items' $fundIds[$role]
        $sale = New-Action 'fixture-funding' 'sell' $fundIds[$role] ([int]$gold['Version'])
        $result = Invoke-Action $role $sale
        Assert-Check ($result['Amount'] -eq 550) "$role earns the normal public quote from funding material"
        $expectedBalances[$role] += [long]$result['Amount']
        $replays.Add(@{ Role = $role; Command = $sale; Result = $result })
    }
    Assert-Balances 'funded'
    Move-To 'host' -2 -3.7 'west raw pickup'
    $null = Item-Action 'host' 'pickup' $rawIds.host
    Move-To 'client' 2 -3.7 'east raw pickup'
    $null = Item-Action 'client' 'pickup' $rawIds.client
    Move-To 'host' 4 -2 'cutter south west'
    Move-To 'client' 6 -3.8 'cutter east approach behind partner'
    Move-To 'client' 6 -2 'cutter south east'
    foreach ($role in @('host', 'client')) {
        $first = Item-Action $role 'inspect' $rawIds[$role]
        $again = Item-Action $role 'inspect' $rawIds[$role]
        Assert-Check ($first['Message'] -ceq $again['Message']) "$role repeated inspection gives one stable clue"
        $stone = Find-Entity (Get-LiveView $role).Data 'Items' $rawIds[$role]
        $clues[$role] = $stone['OwnClue']
        Assert-Check (-not [string]::IsNullOrEmpty($clues[$role]) -and $stone['WasInspected'] -and
            $stone['WindowBand'] -eq 0 -and [string]::IsNullOrEmpty($stone['PublicCore'])) "$role inspection remains private before opening"
        $otherRole = if ($role -eq 'host') { 'client' } else { 'host' }
        $other = Wait-View $otherRole { param($view)
            @($view['Items'] | Where-Object { $_['Id'] -ceq $rawIds[$role] -and $_['WasInspected'] }).Count -eq 1 }
        Assert-Check ([string]::IsNullOrEmpty((Find-Entity $other.Data 'Items' $rawIds[$role])['OwnClue'])) "$otherRole sees inspection status without $role private clue"
        $window = New-Action 'window-once' 'window' $rawIds[$role] ([int]$stone['Version'])
        $result = Invoke-Action $role $window
        $expectedBalances[$role] -= [long]$rules['WindowPrice']
        $windowed = Find-Entity (Get-LiveView $role).Data 'Items' $rawIds[$role]
        $windowBands[$role] = [int]$windowed['WindowBand']
        Assert-Check ($windowed['RevealStage'] -eq 1 -and $windowBands[$role] -in @(1, 2, 3) -and
            $windowed['OwnClue'] -ceq $clues[$role] -and [string]::IsNullOrEmpty($windowed['PublicCore'])) "$role window exposes a stable coarse band, not full truth"
        Send-Command $role $window
        Assert-ResultSame (Wait-Result $role $window.RequestId 2).Entries[1] $result "$role window replay cannot charge twice"
        $replays.Add(@{ Role = $role; Command = $window; Result = $result })
        $null = Item-Action $role 'pocket' $rawIds[$role]
    }
    Assert-Balances 'after-window-fees'

    Move-To 'host' 2.5 -3.8 'west approach around wager table'
    Move-To 'host' 3 -7 'wager west participant'
    Move-To 'client' 7.5 -3.8 'east approach around wager table'
    Move-To 'client' 7 -7 'wager east participant'
    $null = Offer 'host' 'client' $rawIds.host 20
    $version = [long](Get-LiveView 'host').Data['Wager']['Version']; Wait-WagerVersion $version
    $null = Offer 'client' 'host' $rawIds.client 20
    $version = [long](Get-LiveView 'client').Data['Wager']['Version']; Wait-WagerVersion $version
    foreach ($role in @('host', 'client')) {
        $otherRole = if ($role -eq 'host') { 'client' } else { 'host' }
        $view = (Get-LiveView $role).Data
        $offered = Find-Entity $view 'Items' $rawIds[$otherRole]
        Assert-Check ($offered['WasInspected'] -and $offered['RevealStage'] -eq 1 -and
            $offered['WindowBand'] -eq $windowBands[$otherRole] -and [string]::IsNullOrEmpty($offered['OwnClue']) -and
            [string]::IsNullOrEmpty($offered['PublicCore']) -and $view['Wager']['ValueA'] -eq 0 -and $view['Wager']['ValueB'] -eq 0) "$role sees offered pocket stone disclosures without private appraisal or future outcome"
    }
    $null = Invoke-Action 'host' (Wager-Command 'host' 'wager_confirm')
    $null = Wait-View 'client' { param($view) $view['Wager']['ConfirmedA'] -and -not $view['Wager']['ConfirmedB'] }
    $oldVersion = [long](Get-LiveView 'client').Data['Wager']['Version']
    $stake = 25L
    $null = Offer 'client' 'host' $rawIds.client $stake
    foreach ($role in @('host', 'client')) {
        $changed = Wait-View $role { param($view) $view['Wager']['Version'] -gt $oldVersion -and
            -not $view['Wager']['ConfirmedA'] -and -not $view['Wager']['ConfirmedB'] -and $view['Wager']['Stake'] -eq $stake }
        Assert-Check ($changed.Data['Wager']['Phase'] -eq 0) "$role observes changed stake clearing both confirmations"
    }
    Assert-Balances 'draft-no-escrow-deductions'
    $null = Invoke-Action 'host' (Wager-Command 'host' 'wager_confirm')
    $null = Wait-View 'client' { param($view) $view['Wager']['ConfirmedA'] -and -not $view['Wager']['ConfirmedB'] }
    $lock = Wager-Command 'client' 'wager_confirm'
    $lockResult = Invoke-Action 'client' $lock
    $replays.Add(@{ Role = 'client'; Command = $lock; Result = $lockResult })
    if ($CrashOnWagerLock) { Recover-FromLockedWager $lock $lockResult }
    $settled = Wait-View 'client' { param($view) $null -ne $view['Wager'] -and $view['Wager']['Phase'] -eq 2 }
    $wagerId = [string]$settled.Data['Wager']['Id']
    Assert-Check ($settled.Data['Wager']['ValueA'] -eq 120 -and $settled.Data['Wager']['ValueB'] -eq 240 -and
        $settled.Data['Wager']['WinnerId'] -ceq $playerIds.client) 'Fixed original stone values determine the wager winner'
    $expectedBalances.host -= $stake
    $expectedBalances.client += $stake
    Assert-Balances 'settled'
    Assert-Check (($expectedBalances.host + $expectedBalances.client) -eq
        ($initialBalances.host + $initialBalances.client + 1100 - 2 * [long]$rules['WindowPrice'])) 'Wager conserves both stakes; only the two normal processing fees leave circulation'
    foreach ($role in @('host', 'client')) {
        $returned = Wait-Item $role $rawIds[$role] 4 $playerIds[$role]
        $stone = Find-Entity $returned.Data 'Items' $rawIds[$role]
        $settledVersions[$role] = [int]$stone['Version']
        Assert-Check ($stone['RevealStage'] -eq 2 -and $stone['PublicCore'] -ceq 'gold' -and
            $stone['SaleQuote'] -eq $fixtureValues[$role] -and $stone['OwnClue'] -ceq $clues[$role] -and
            $stone['WindowBand'] -eq $windowBands[$role]) "$role receives its original opened stone and unchanged inspection/window data"
        $otherRole = if ($role -eq 'host') { 'client' } else { 'host' }
        Assert-Check (@($returned.Data['Items'] | Where-Object { $_['Id'] -ceq $rawIds[$otherRole] }).Count -eq 0) "$role cannot inspect the other participant's private cabinet"
    }
    $departing = (Get-LiveView 'client').Data
    $departureSnapshot = @{
        Position = (Get-Miner $departing)['Position']
        ItemIds = @($departing['Items'] | Where-Object { $_['OwnerId'] -ceq $playerIds.client -and
            $_['Place'] -in @(1, 2, 3) -and $_['Kind'] -ne 3 } | ForEach-Object { $_['Id'] })
    }
    Assert-Check ($departureSnapshot.ItemIds.Count -ge 2) 'Client carries two existing small cargo IDs before normal departure'
    $evidence.Scenario['DepartureCargo'] = $departureSnapshot
    Stop-Player 'client'
    Wait-GameSeconds 1.5
    $offlineBag = Assert-OfflineBag (Get-LiveView 'host').Data 'After normal client departure'
    $evidence.Scenario['OfflineBag'] = $offlineBag
    Stop-Player 'host'
    Assert-SocialState (Read-World $worldId) 'Before restart'
    foreach ($role in @('host', 'client')) { Archive-ControlFiles $role }
    Start-Player 'host' 'reopened' $false
    $null = Get-LiveView 'host'
    Start-Player 'client' 'reopened' $false
    foreach ($role in @('host', 'client')) {
        $view = Wait-View $role { param($data) $data['WorldId'] -ceq $worldId -and
            $data['Self']['Id'] -ceq $playerIds[$role] -and @($data['Players'] | Where-Object { $_['Online'] }).Count -eq 2 -and
            $null -ne $data['Wager'] -and $data['Wager']['Id'] -ceq $wagerId -and $data['Wager']['Phase'] -eq 2 }
        $stone = Find-Entity $view.Data 'Items' $rawIds[$role]
        Assert-Check ($stone['Place'] -eq 4 -and $stone['Version'] -eq $settledVersions[$role] -and
            $stone['RevealStage'] -eq 2 -and $stone['SaleQuote'] -eq $fixtureValues[$role] -and
            $stone['OwnClue'] -ceq $clues[$role] -and $stone['WindowBand'] -eq $windowBands[$role]) "$role reopening preserves same stone, price and private clue"
    }
    Wait-GameSeconds 1.5
    foreach ($role in @('host', 'client')) {
        $null = Assert-OfflineBag (Get-LiveView $role).Data "Reopened $role" $offlineBag
    }
    foreach ($replay in $replays) {
        Send-Command $replay.Role $replay.Command
        Assert-ResultSame (Wait-Result $replay.Role $replay.Command.RequestId).Entries[0] $replay.Result "Reopened $($replay.Role) $($replay.Command.Action) returns its one durable receipt"
    }
    Assert-Balances 'reopened-idempotent'
    Stop-Player 'client'
    Stop-Player 'host'
    Assert-SocialState (Read-World $worldId) 'After restart'
    foreach ($entry in $processes) {
        $errors = @(Select-String -LiteralPath $entry.LogPath -Pattern 'NullReferenceException|MissingReferenceException|Unhandled Exception|营地退出保存失败|无法打开营地' -ErrorAction Stop)
        Assert-Check ($errors.Count -eq 0) "$($entry.Role)/$($entry.Phase) player log has no matching runtime/save failures" 'Log contents are retained locally, not printed.'
    }
    $evidence.Scenario['FixtureOnly'] = $true
    $evidence.Scenario['WorldId'] = $worldId
    $evidence.Scenario['PlayerIds'] = $playerIds
    $evidence.Scenario['RawIds'] = $rawIds
    $evidence.Scenario['WagerId'] = $wagerId
    $evidence.Scenario['Stake'] = $stake
    $evidence.Scenario['FinalBalances'] = $expectedBalances
    $evidence.Scenario['SettledItemVersions'] = $settledVersions
    $evidence.Success = $true
} catch {
    $evidence.Error = $_.Exception.Message
    $evidence.Scenario['FailureLine'] = $_.InvocationInfo.ScriptLineNumber
} finally {
    # Stop only handles created by this script. Never identify processes by name.
    foreach ($entry in $processes) {
        if (-not $entry.Process.HasExited) {
            $entry.ExpectedRunning = $false
            try { Set-Neutral $entry.Role 'failure-neutral'; Send-Command $entry.Role (New-Command 'c-cleanup-shutdown' 'shutdown') } catch { }
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
            ExitCode = $exitCode; ForcedTermination = $entry.Forced; IntentionalCrash = $entry.IntentionalCrash; LogPath = $entry.LogPath; TestRoot = $entry.Root
        }
    }
    $evidence.FinishedUtc = [DateTime]::UtcNow.ToString('O')
    $evidencePath = Join-Path $runRoot 'evidence.json'
    Write-AtomicText $evidencePath (ConvertTo-Json -InputObject $evidence -Depth 40)
}
[pscustomobject]@{ Success = $evidence.Success; EvidencePath = $evidencePath; RunRoot = $runRoot; Checks = $checks.Count; Port = $evidence.Port }
if (-not $evidence.Success) { throw "Demo social validation failed. Evidence: $evidencePath" }
