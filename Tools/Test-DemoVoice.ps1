#requires -Version 7.0
<#
.SYNOPSIS
Exercises checkpoint E using two real FishNet players and networked movement axes.
.DESCRIPTION
Creates fresh, marked test roots under .local/TestRuns; never accepts a save path.
CampTestDriver consumes inbox.jsonl. commands.jsonl is an identical audit copy.
Exercises two-way synthetic tone, local mute, distance routing, playback callbacks and optional microphone capture.
Always launches graphical 960x600 Development players. It never records audio or changes system audio settings.
This is automated loopback audio API validation; human speech, hearing quality, LAN and Steam remain separate.
Use -ValidateInputMathOnly to check movement calculations without files or player processes.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 300)] [int]$TimeoutSeconds = 90,
    [switch]$SkipMicrophone,
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
    Suite = 'E-voice-graphical-two-process-loopback'
    Mode = 'Graphical 960x600 players; live audio callbacks; no batchmode or nographics'
    NotValidated = @('human conversation', 'human hearing quality', 'human input', 'LAN devices', 'cross-network play', 'Steam transport', 'four humans')
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
    if ($suiteTimer.Elapsed.TotalMinutes -gt 12) { throw 'Voice suite exceeded its 12-minute hard deadline.' }
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
    $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Normal
    $arguments = [Collections.Generic.List[string]]::new()
    foreach ($argument in @('-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '600')) { $arguments.Add($argument) }
    foreach ($argument in @('-logFile', $logPath, '--gc-test-root', $root, '--gc-port', [string]$evidence.Port)) {
        $arguments.Add($argument)
    }
    if ($Role -eq 'host') {
        $arguments.Add('--gc-host')
        if ($NewWorld) { $arguments.Add('--gc-new'); $arguments.Add('--gc-seed'); $arguments.Add([string]$voiceSeed) }
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
    Send-Command $Role (New-Command "e-shutdown-$Role-$($entry.Phase)" 'shutdown')
    $entry.ExpectedRunning = $false
    $null = Wait-Until "$Role/$($entry.Phase) normal exit" {
        if ($entry.Process.HasExited) { return $true }
        return $null
    }
    $entry.Process.WaitForExit()
    Assert-Check ($entry.Process.ExitCode -eq 0) "$Role/$($entry.Phase) exited normally" "PID $($entry.Id); exit $($entry.Process.ExitCode)"
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

$voiceSeed = 6
$commandNumber = 0
$evidence.Scenario['Seed'] = $voiceSeed
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
    $command = New-Command ("e-$Label-" + $script:commandNumber) $Action $Target $Version
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

$voiceCounters = @('CapturedSamples', 'SentPackets', 'ReceivedPackets', 'DecodedPackets', 'RejectedPackets', 'PlayedSamples', 'PlayedNonzero', 'CallbackRequestedSamples', 'CallbackMaximumSamples', 'RingDroppedSamples')
$evidence.Scenario['Audio'] = [Collections.Generic.List[object]]::new()
$evidence.Scenario['Microphone'] = [ordered]@{ Status = 'not_run'; DeviceCount = $null; CaptureTransportVerified = $false }
$evidence.Scenario['RealConversation'] = 'not_validated'
$evidence.Scenario['AudioFilesSaved'] = $false
$evidence['ValidationStatus'] = 'not_validated'
$evidence['MicrophoneValidated'] = $false

function Wait-Voice([string]$VoiceRole, [scriptblock]$Condition) {
    $voiceRoot = $roles[$VoiceRole].Root
    return Wait-Until "$VoiceRole voice diagnostics" {
        $voiceSnapshot = Read-JsonSnapshot (Join-Path $voiceRoot 'voice.json')
        if ($null -eq $voiceSnapshot) { return $null }
        foreach ($field in @('Mode', 'Status', 'Device', 'Devices', 'Speakers', 'Capturing', 'Level', 'Stream') + $voiceCounters) {
            if (-not $voiceSnapshot.Data.Contains($field)) { throw "Voice diagnostics lack $field; build the E Development player before this script." }
        }
        if (& $Condition $voiceSnapshot.Data) { return $voiceSnapshot.Data }
        return $null
    }
}
function Read-VoicePair {
    $pair = @{}
    foreach ($voiceRole in @('host', 'client')) {
        $pair[$voiceRole] = Wait-Voice $voiceRole { param($voice) $voice['Mode'] -in @('off', 'tone', 'mic') }
        foreach ($counter in $voiceCounters) {
            if ([long]$pair[$voiceRole][$counter] -lt 0) { throw "$voiceRole voice counter $counter is negative." }
        }
    }
    return $pair
}
function Record-VoicePair([string]$Label, $Pair) {
    $row = [ordered]@{ Label = $Label; Utc = [DateTime]::UtcNow.ToString('O'); Roles = [ordered]@{} }
    foreach ($voiceRole in @('host', 'client')) {
        $voice = $Pair[$voiceRole]
        $safe = [ordered]@{
            Mode = $voice['Mode']; Status = $voice['Status']; Devices = $voice['Devices']; Speakers = $voice['Speakers']
            Capturing = $voice['Capturing']; Level = $voice['Level']; Stream = [int]$voice['Stream']
            ExplicitDeviceSelected = -not [string]::IsNullOrEmpty($voice['Device'])
        }
        foreach ($counter in $voiceCounters) { $safe[$counter] = [long]$voice[$counter] }
        $row.Roles[$voiceRole] = $safe
    }
    $evidence.Scenario.Audio.Add($row)
}
function Voice-Control([string]$Role, [ValidateSet('off', 'tone', 'mic')][string]$Mode, [string]$MutedPlayer = '') {
    $command = New-Action "voice-$Mode" 'voice_test' $Mode
    $command.OtherPlayerId = $MutedPlayer
    # This is a local Development-driver acknowledgement, not a world transaction.
    # It proves even an off -> off mute change was consumed before measuring.
    $null = Invoke-Action $Role $command
    $null = Wait-Voice $Role { param($voice) $voice['Mode'] -ceq $Mode }
}
function Stop-BothVoices {
    foreach ($voiceRole in @('host', 'client')) { Voice-Control $voiceRole 'off' }
}
function Wait-VoiceSilence([string]$Label) {
    # Jitter expires after .5 s; the PCM ring holds at most .2 s. Let both drain.
    Wait-GameSeconds 1.2
    $previous = Read-VoicePair
    $silenceTimer = [Diagnostics.Stopwatch]::StartNew()
    while ($silenceTimer.Elapsed.TotalSeconds -lt 5) {
        Wait-GameSeconds .5
        $current = Read-VoicePair
        $stable = $true
        foreach ($voiceRole in @('host', 'client')) {
            if ($current[$voiceRole]['Mode'] -cne 'off' -or $current[$voiceRole]['Capturing']) { $stable = $false }
            foreach ($counter in @('CapturedSamples', 'SentPackets', 'ReceivedPackets', 'DecodedPackets', 'PlayedNonzero')) {
                if ([long]$current[$voiceRole][$counter] -ne [long]$previous[$voiceRole][$counter]) { $stable = $false }
            }
        }
        if ($stable) { Record-VoicePair $Label $current; return $current }
        $previous = $current
    }
    Record-VoicePair "$Label-unstable" $previous
    throw "Voice counters did not settle after stopping capture: $Label."
}
function Assert-Growth($Before, $After, [string]$Role, [string[]]$Fields, [string]$Label) {
    foreach ($field in $Fields) {
        $delta = [long]$After[$Role][$field] - [long]$Before[$Role][$field]
        Assert-Check ($delta -gt 0) "$Label $Role $field increased" "delta=$delta"
    }
}
function Assert-ReceiveBlocked($Before, $After, [string]$Role, [string]$Label) {
    foreach ($field in @('ReceivedPackets', 'DecodedPackets')) {
        $delta = [long]$After[$Role][$field] - [long]$Before[$Role][$field]
        Assert-Check ($delta -eq 0) "$Label $Role $field stayed unchanged" "delta=$delta"
    }
}
function Run-OneWayTone([string]$Label, [bool]$Blocked = $false) {
    $before = Wait-VoiceSilence "$Label-baseline"
    Voice-Control 'host' 'tone'
    $null = Wait-Voice 'host' { param($voice) $voice['Mode'] -ceq 'tone' -and $voice['Capturing'] }
    Wait-GameSeconds 4
    $active = Read-VoicePair
    Record-VoicePair "$Label-active" $active
    Assert-Check ($active.host['Capturing']) "$Label host kept producing synthetic audio during the measurement"
    Voice-Control 'host' 'off'
    $after = Wait-VoiceSilence "$Label-stopped"
    Assert-Growth $before $after 'host' @('SentPackets') $Label
    if ($Blocked) { Assert-ReceiveBlocked $before $after 'client' $Label }
    else { Assert-Growth $before $after 'client' @('ReceivedPackets', 'DecodedPackets', 'PlayedSamples', 'PlayedNonzero') $Label }
}
function Read-ContinuousTone([int]$ExpectedStream, [string]$Label) {
    $pair = Read-VoicePair
    Assert-Check ($pair.host['Mode'] -ceq 'tone' -and $pair.host['Capturing']) "$Label host is still capturing tone"
    Assert-Check ($ExpectedStream -gt 0 -and [int]$pair.host['Stream'] -eq $ExpectedStream) "$Label host stream is unchanged" "expected=$ExpectedStream; actual=$($pair.host['Stream'])"
    Record-VoicePair $Label $pair
    return $pair
}
function Player-Distance {
    $view = (Get-LiveView 'host').Data
    $a = (Find-Entity $view 'Players' $playerIds.host)['Position']
    $b = (Find-Entity $view 'Players' $playerIds.client)['Position']
    $dx = [double]$a['x'] - [double]$b['x']; $dy = [double]$a['y'] - [double]$b['y']; $dz = [double]$a['z'] - [double]$b['z']
    return [Math]::Sqrt($dx * $dx + $dy * $dy + $dz * $dz)
}

try {
    foreach ($role in @('host', 'client')) {
        $root = Assert-TestPath (Join-Path $runRoot $role)
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Write-AtomicText (Join-Path $root '.goldcamp-test-root') 'GoldCamp E isolated graphical voice test; no audio recording; never a human save.'
        $roles[$role] = @{ Root = $root; InboxLines = [Collections.Generic.List[string]]::new(); CurrentProcess = $null; LastYaw = 0.0 }
    }
    $reservation = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $reservation.Client.ExclusiveAddressUse = $true
        $reservation.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
        $evidence.Port = ([Net.IPEndPoint]$reservation.Client.LocalEndPoint).Port
    } finally { $reservation.Dispose() }
    Start-Player 'host' 'voice' $true
    $null = Get-LiveView 'host'
    Start-Player 'client' 'voice' $false
    $initial = @{}
    foreach ($role in @('host', 'client')) {
        $initial[$role] = Wait-View $role { param($view) @($view['Players'] | Where-Object { $_['Online'] }).Count -eq 2 }
    }
    $playerIds = @{ host = $initial.host.Data['Self']['Id']; client = $initial.client.Data['Self']['Id'] }
    $worldId = [string]$initial.host.Data['WorldId']
    Assert-Check ($roles.host.CurrentProcess.Id -ne $roles.client.CurrentProcess.Id -and $playerIds.host -cne $playerIds.client) 'Two distinct graphical player processes and identities'
    Assert-Check ($initial.client.Data['WorldId'] -ceq $worldId) 'Both voice peers joined one host world before testing'
    foreach ($role in @('host', 'client')) { Assert-Projection $initial[$role] $playerIds[$role] "initial-$role" }
    Move-To 'host' 0 -4 'near voice host in central clearing'
    Move-To 'client' 2 -4 'near voice client beside host'
    Assert-Check ((Player-Distance) -lt 3) 'Near speakers are inside full-volume range through real movement'
    Stop-BothVoices
    $baseline = Wait-VoiceSilence 'initial-stopped'
    foreach ($role in @('host', 'client')) { Voice-Control $role 'tone' }
    foreach ($role in @('host', 'client')) {
        $null = Wait-Voice $role { param($voice) $voice['Mode'] -ceq 'tone' -and $voice['Capturing'] }
    }
    Wait-GameSeconds 4
    $bothActive = Read-VoicePair
    Record-VoicePair 'two-way-tone-active' $bothActive
    foreach ($role in @('host', 'client')) { Assert-Check ($bothActive[$role]['Capturing']) "$role synthetic source remains active for two-way test" }
    Stop-BothVoices
    $bothAfter = Wait-VoiceSilence 'two-way-tone-stopped'
    foreach ($role in @('host', 'client')) {
        Assert-Growth $baseline $bothAfter $role @('SentPackets', 'ReceivedPackets', 'DecodedPackets', 'PlayedSamples', 'PlayedNonzero') 'two-way synthetic audio'
        $decodedSamples = ([long]$bothAfter[$role]['DecodedPackets'] - [long]$baseline[$role]['DecodedPackets']) * 320
        $playedNonzero = [long]$bothAfter[$role]['PlayedNonzero'] - [long]$baseline[$role]['PlayedNonzero']
        $dropped = [long]$bothAfter[$role]['RingDroppedSamples'] - [long]$baseline[$role]['RingDroppedSamples']
        Assert-Check ($decodedSamples -gt 0 -and $playedNonzero -ge .85 * $decodedSamples) "$role mixer consumes most decoded tone samples" "nonzero=$playedNonzero; decoded=$decodedSamples; audio API continuity, not human hearing quality"
        Assert-Check ($dropped -le .05 * $decodedSamples) "$role PCM ring does not discard sustained tone through reader prefetch" "dropped=$dropped; decoded=$decodedSamples"
    }
    $evidence.Scenario['TwoWayTone'] = 'passed_transport_decode_and_nonzero_playback_callbacks'

    # SetTestMode replaces the single test mute selection; off + empty clears it.
    Voice-Control 'client' 'off' $playerIds.host
    Run-OneWayTone 'receiver locally muted sender' $true
    Voice-Control 'client' 'off' ''
    Run-OneWayTone 'receiver unmuted sender'

    Stop-BothVoices
    $null = Wait-VoiceSilence 'before-distance-route'
    # The sale counter occupies x0,z0. Walk around its east side, then use the
    # central flat lane ending three metres before the starter pit at z20.
    Move-To 'client' 2 2 'voice range route around sale counter'
    Move-To 'client' 0 3 'voice range route back to central lane'
    Move-To 'client' 0 17 'voice range far point before the mine'
    $farDistance = Player-Distance
    if ($farDistance -le 20.5) {
        # A small supported south step makes the boundary unambiguous without
        # entering the mine or changing player coordinates directly.
        Move-To 'host' 0 -5.2 'voice range southern margin'
        $farDistance = Player-Distance
    }
    Assert-Check ($farDistance -gt 20) 'Actual player distance exceeds the voice range' "distance=$farDistance"
    $evidence.Scenario['FarDistance'] = $farDistance
    Run-OneWayTone 'server proximity blocks distant receiver' $true
    Move-To 'client' 0 3 'return from distant voice point'
    Move-To 'client' 2 2 'return around sale counter'
    Move-To 'client' 2 -4 'return to nearby voice position'
    Move-To 'host' 0 -4 'restore central near position'
    Assert-Check ((Player-Distance) -lt 3) 'Actual movement restores nearby voice distance'
    Run-OneWayTone 'proximity resumes after walking nearby'

    # Keep the same capture generation alive over the entire route. Restarting tone
    # would create a new Stream and conceal a same-stream timeout recovery defect.
    $continuousBefore = Wait-VoiceSilence 'continuous-range-before-capture'
    Voice-Control 'host' 'tone'
    $continuousStarted = Wait-Voice 'host' { param($voice)
        $voice['Mode'] -ceq 'tone' -and $voice['Capturing'] -and [int]$voice['Stream'] -gt 0 }
    $continuousStream = [int]$continuousStarted['Stream']
    $evidence.Scenario['ContinuousRange'] = [ordered]@{ Stream = $continuousStream; Status = 'started'; FarDistance = $null }
    Wait-GameSeconds 1.2
    $continuousNear = Read-ContinuousTone $continuousStream 'continuous-range-initial-near'
    Assert-Growth $continuousBefore $continuousNear 'client' @('ReceivedPackets', 'DecodedPackets', 'PlayedNonzero') 'continuous range initial audio'

    Move-To 'client' 2 2 'continuous voice route around sale counter'
    $null = Read-ContinuousTone $continuousStream 'continuous-range-outbound-east'
    Move-To 'client' 0 3 'continuous voice route central lane'
    $null = Read-ContinuousTone $continuousStream 'continuous-range-outbound-lane'
    Move-To 'client' 0 17 'continuous voice far point before mine'
    $null = Read-ContinuousTone $continuousStream 'continuous-range-outbound-far'
    $continuousDistance = Player-Distance
    if ($continuousDistance -le 20.5) {
        Move-To 'host' 0 -5.2 'continuous voice southern range margin'
        $null = Read-ContinuousTone $continuousStream 'continuous-range-host-margin'
        $continuousDistance = Player-Distance
    }
    Assert-Check ($continuousDistance -gt 20) 'Continuous stream receiver is beyond 20 metres' "distance=$continuousDistance"
    $evidence.Scenario.ContinuousRange.FarDistance = $continuousDistance
    # Capture continues while .5 s jitter history, .2 s PCM and the streaming clip
    # tail expire. Measure a second full interval, excluding that legitimate tail.
    Wait-GameSeconds 1.2
    $continuousFarBefore = Read-ContinuousTone $continuousStream 'continuous-range-far-drained'
    Wait-GameSeconds 1.2
    $continuousFarAfter = Read-ContinuousTone $continuousStream 'continuous-range-far-stable'
    Assert-Check ((Player-Distance) -gt 20) 'Continuous stream receiver stayed outside range during measurement'
    Assert-Growth $continuousFarBefore $continuousFarAfter 'host' @('SentPackets') 'continuous range far sender'
    Assert-ReceiveBlocked $continuousFarBefore $continuousFarAfter 'client' 'continuous range far receiver'
    Assert-Check ([long]$continuousFarAfter.client['PlayedNonzero'] -eq [long]$continuousFarBefore.client['PlayedNonzero']) 'Continuous stream far receiver has no residual nonzero playback'

    Move-To 'client' 0 3 'continuous voice return central lane'
    $null = Read-ContinuousTone $continuousStream 'continuous-range-return-lane'
    Move-To 'client' 2 2 'continuous voice return around sale counter'
    $null = Read-ContinuousTone $continuousStream 'continuous-range-return-east'
    Move-To 'client' 2 -4 'continuous voice return nearby'
    $null = Read-ContinuousTone $continuousStream 'continuous-range-return-near'
    Move-To 'host' 0 -4 'continuous voice restore central host'
    $null = Read-ContinuousTone $continuousStream 'continuous-range-host-restored'
    Assert-Check ((Player-Distance) -lt 3) 'Continuous stream receiver returned to full-volume range'
    Wait-GameSeconds 1.2
    $continuousRecovered = Read-ContinuousTone $continuousStream 'continuous-range-recovered-without-restart'
    Assert-Growth $continuousFarAfter $continuousRecovered 'host' @('SentPackets') 'continuous range uninterrupted sender'
    Assert-Growth $continuousFarAfter $continuousRecovered 'client' @('ReceivedPackets', 'DecodedPackets', 'PlayedSamples', 'PlayedNonzero') 'continuous range same-stream recovery'
    Voice-Control 'host' 'off'
    $continuousStopped = Wait-VoiceSilence 'continuous-range-stopped'
    Assert-Check ([int]$continuousStopped.host['Stream'] -eq $continuousStream) 'Continuous range capture ended without changing its stream'
    $evidence.Scenario.ContinuousRange.Status = 'passed_same_stream_timeout_range_recovery'

    Stop-BothVoices
    $micBefore = Wait-VoiceSilence 'before-real-microphone'
    $deviceCount = [int]$micBefore.host['Devices']
    $evidence.Scenario.Microphone.DeviceCount = $deviceCount
    if ($SkipMicrophone) {
        $evidence.Scenario.Microphone.Status = 'not_validated_requested_skip'
    } elseif ($deviceCount -le 0) {
        $evidence.Scenario.Microphone.Status = 'not_validated_no_device'
    } else {
        Write-Host 'Voice test: capturing the host microphone for about 3 seconds; no audio file is saved.'
        Voice-Control 'host' 'mic'
        $null = Wait-Voice 'host' { param($voice) $voice['Mode'] -ceq 'mic' -and $voice['Capturing'] }
        Wait-GameSeconds 3
        $micActive = Read-VoicePair
        Record-VoicePair 'real-microphone-active' $micActive
        Assert-Check ($micActive.host['Capturing']) 'Real microphone stays active during API transport measurement'
        Voice-Control 'host' 'off'
        $micAfter = Wait-VoiceSilence 'real-microphone-stopped'
        Assert-Growth $micBefore $micAfter 'host' @('CapturedSamples', 'SentPackets') 'real microphone API'
        Assert-Growth $micBefore $micAfter 'client' @('ReceivedPackets', 'DecodedPackets', 'PlayedSamples') 'real microphone API'
        $evidence.Scenario.Microphone.Status = 'passed_capture_transport_decode_callbacks'
        $evidence.Scenario.Microphone.CaptureTransportVerified = $true
        $evidence.MicrophoneValidated = $true
    }
    Stop-BothVoices
    $finalVoice = Wait-VoiceSilence 'final-stopped'
    foreach ($role in @('host', 'client')) {
        Assert-Check (-not $finalVoice[$role]['Capturing'] -and $finalVoice[$role]['Mode'] -ceq 'off') "$role has stopped audio capture before exit"
        $view = Get-LiveView $role
        Assert-Projection $view $playerIds[$role] "final-$role"
        Assert-Check ($view.Data['Self']['Balance'] -eq $initial[$role].Data['Self']['Balance'] -and
            @($view.Data['Cells'] | Where-Object { $_['Dug'] }).Count -eq 0) "$role voice and movement checks did not change money or dig the mine"
    }
    Stop-Player 'client'; Wait-GameSeconds .8; Stop-Player 'host'
    foreach ($entry in $processes) {
        $errors = @(Select-String -LiteralPath $entry.LogPath -Pattern 'NullReferenceException|MissingReferenceException|InvalidOperationException|IndexOutOfRangeException|DllNotFoundException|GOLDCAMP.*(FAILED|ERROR)' -ErrorAction Stop)
        Assert-Check ($errors.Count -eq 0) "$($entry.Role) voice player has no matching runtime exceptions" 'Only the count and log path are retained; log contents are not copied.'
    }
    $recordings = @(Get-ChildItem -LiteralPath $runRoot -Recurse -File | Where-Object { $_.Extension -in @('.wav', '.mp3', '.ogg', '.opus', '.pcm', '.flac', '.aif', '.aiff') })
    Assert-Check ($recordings.Count -eq 0) 'The isolated run contains no recorded audio files'
    $evidence.ValidationStatus = if ($evidence.MicrophoneValidated) { 'passed_audio_api_checks_real_conversation_unverified' } else { 'passed_tone_checks_microphone_pending' }
    $evidence.Success = $true

} catch {
    $evidence.Error = $_.Exception.Message
    $evidence.Scenario['FailureLine'] = $_.InvocationInfo.ScriptLineNumber
} finally {
    # Stop only handles created by this script. Never identify processes by name.
    foreach ($entry in $processes) {
        if (-not $entry.Process.HasExited) {
            $entry.ExpectedRunning = $false
            try { Set-Neutral $entry.Role 'failure-neutral'; Send-Command $entry.Role (New-Command 'e-cleanup-shutdown' 'shutdown') } catch { }
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
[pscustomobject]@{ Success = $evidence.Success; EvidencePath = $evidencePath; RunRoot = $runRoot; Checks = $checks.Count; Port = $evidence.Port; ValidationStatus = $evidence.ValidationStatus; MicrophoneValidated = $evidence.MicrophoneValidated; RealConversationValidated = $false }
if (-not $evidence.Success) { throw "Demo voice validation failed. Evidence: $evidencePath" }
