#requires -Version 7.0
<#
.SYNOPSIS
Exercises checkpoint B using two real FishNet players and networked movement axes.
.DESCRIPTION
Creates fresh, marked test roots under .local/TestRuns; never accepts a save path.
CampTestDriver consumes inbox.jsonl. commands.jsonl is an identical audit copy.
Mines two layers, climbs, carries actual heavy ore, assists, sells, and reopens the camp.
This is automated loopback validation, not human input, LAN, Steam, or voice testing.
Use -ValidateInputMathOnly to check movement calculations without files or player processes.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 300)] [int]$TimeoutSeconds = 90,
    [switch]$WithGraphics,
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
$processes = [Collections.Generic.List[object]]::new()
$checks = [Collections.Generic.List[object]]::new()
$roles = @{}
$evidence = [ordered]@{
    SchemaVersion = 1
    Suite = 'B-mining-input-two-process-loopback'
    Mode = $(if ($WithGraphics) { 'Automated player processes with graphics' } else { 'Automated -batchmode -nographics player processes' })
    NotValidated = @('human input', 'LAN devices', 'cross-network play', 'Steam', 'voice', 'four humans')
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

function Wait-View([string]$Role, [scriptblock]$Condition) {
    $root = $roles[$Role].Root
    return Wait-Until "$Role client projection" {
        $snapshot = Read-JsonSnapshot (Join-Path $root 'view.json')
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
        if ($NewWorld) { $arguments.Add('--gc-new'); $arguments.Add('--gc-seed'); $arguments.Add([string]$miningSeed) }
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
    Send-Command $Role (New-Command "b-shutdown-$Role-$($entry.Phase)" 'shutdown')
    $entry.ExpectedRunning = $false
    $null = Wait-Until "$Role/$($entry.Phase) normal exit" {
        if ($entry.Process.HasExited) { return $true }
        return $null
    }
    $entry.Process.WaitForExit()
    Assert-Check ($entry.Process.ExitCode -eq 0) "$Role/$($entry.Phase) exited normally" "PID $($entry.Id); exit $($entry.Process.ExitCode)"
}

function Archive-ControlFiles([string]$Role) {
    $entry = $roles[$Role]
    foreach ($file in @('view.json', 'results.jsonl', 'inbox.jsonl', 'commands.jsonl', 'process.json', 'input.json', 'inputs.jsonl')) {
        $source = Assert-TestPath (Join-Path $entry.Root $file)
        $destination = Assert-TestPath (Join-Path $entry.Root ("first-$file"))
        if (Test-Path -LiteralPath $source) { Move-Item -LiteralPath $source -Destination $destination }
    }
    $entry.InboxLines.Clear()
}

function Test-ProjectionFields($Node, [string]$Path = '$') {
    if ($Node -is [Collections.IDictionary]) {
        foreach ($key in $Node.Keys) {
            if ($key -in @('CredentialHash', 'HiddenValue', 'Secret')) { return $false }
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

# System.Random(6).Next(100) = 86, in the default HeavyOre interval [85, 93).
# The real public yield must still confirm HeavyOre; no save or hidden state is used to choose it.
$miningSeed = 6
$commandNumber = 0
$topCell = 'starter:0:0:0'
$lowerCell = 'starter:0:1:0'
$heavyId = 'yield:starter:0:0:0:0'
$evidence.Scenario['Seed'] = $miningSeed
$evidence.Scenario['InputProtocol'] = 'input.json -> CampTestDriver -> SendInput; driver assigns Sequence'
$evidence.Scenario['Movement'] = [Collections.Generic.List[object]]::new()
$evidence.Scenario['SpeedSamples'] = [Collections.Generic.List[object]]::new()

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
    $command = New-Command ("b-$Label-" + $script:commandNumber) $Action $Target $Version
    if ($null -ne $Direction) { $command.Direction = $Direction }
    return $command
}
function Invoke-Action([string]$Role, $Command, [bool]$Success = $true) {
    Send-Command $Role $Command
    $result = (Wait-Result $Role $Command.RequestId).Entries[0]
    Assert-Check ($result['Success'] -eq $Success) "$Role $($Command.Action) $($Command.RequestId)" ("code=" + $result['Code'] + "; " + $result['Message'])
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

function Mine-Cell([string]$CellId, [int]$Depth, [int]$MinimumHits) {
    $hitCount = 0
    while ($hitCount -lt 12) {
        Wait-GameSeconds ([double]$rules['MineInterval'] + .22)
        $snapshot = (Get-LiveView 'host').Data
        $cell = Find-Entity $snapshot 'Cells' $CellId
        if ($cell['Dug']) { break }
        $direction = Aim-At (Get-Miner $snapshot)['Position'] -5 (-1 - 2 * $Depth) 21
        $null = Invoke-Action 'host' (New-Action "mine-$Depth-$hitCount" 'mine' $CellId ([int]$cell['Version']) $direction)
        $next = Wait-View 'host' { param($view)
            @($view['Cells'] | Where-Object { $_['Id'] -ceq $CellId -and $_['Version'] -gt $cell['Version'] }).Count -eq 1 }
        $changed = Find-Entity $next.Data 'Cells' $CellId
        Assert-Check ($changed['Version'] -eq ([int]$cell['Version'] + 1) -and
            [double]$changed['Health'] -lt [double]$cell['Health']) "Depth $Depth hit $hitCount changed authoritative health/version"
        $hitCount++
        if ($changed['Dug']) { break }
    }
    $cell = Find-Entity (Get-LiveView 'host').Data 'Cells' $CellId
    Assert-Check ($cell['Dug'] -and $hitCount -ge $MinimumHits) "Depth $Depth dug through repeated real network hits" "hits=$hitCount"
    $evidence.Scenario["Depth$($Depth)HitCount"] = $hitCount
}

function Assert-BlockedMine([string]$CellId, [double]$X, [double]$Y, [double]$Z, [string]$Label) {
    Wait-GameSeconds ([double]$rules['MineInterval'] + .22)
    $snapshot = (Get-LiveView 'host').Data
    $before = Find-Entity $snapshot 'Cells' $CellId
    $direction = Aim-At (Get-Miner $snapshot)['Position'] $X $Y $Z
    $result = Invoke-Action 'host' (New-Action $Label 'mine' $CellId ([int]$before['Version']) $direction) $false
    Assert-Check ($result['Code'] -in @('blocked', 'aim')) "$Label rejected for physical reach or covering rock" ("code=" + $result['Code'])
    Wait-GameSeconds .3
    foreach ($role in @('host', 'client')) {
        $after = Find-Entity (Get-LiveView $role).Data 'Cells' $CellId
        Assert-Check ($after['Version'] -eq $before['Version'] -and $after['Health'] -eq $before['Health'] -and -not $after['Dug']) "$Label did not damage covered cell ($role view)"
    }
}
function Place-Ladder([string]$CellId) {
    $cell = Find-Entity (Get-LiveView 'host').Data 'Cells' $CellId
    $null = Invoke-Action 'host' (New-Action 'place-ladder' 'place_ladder' $CellId ([int]$cell['Version']))
    $null = Wait-View 'host' { param($view) @($view['Ladders'] | Where-Object { $_['Id'] -ceq "ladder:$CellId" }).Count -eq 1 }
}
function Attach-Ladder([string]$CellId) {
    $ladder = Find-Entity (Get-LiveView 'host').Data 'Ladders' "ladder:$CellId"
    $null = Invoke-Action 'host' (New-Action 'attach' 'climb' $ladder['Id'] ([int]$ladder['Version']))
    $null = Wait-View 'host' { param($view) (Get-Miner $view)['ClimbingLadderId'] -ceq $ladder['Id'] }
}
function Climb-To([double]$Y, [string]$Label) {
    $from = (Get-Miner (Get-LiveView 'host').Data)['Position']
    $arrivalTolerance = if ($Y -ge 0) { .018 } else { .12 }
    try {
        $null = Wait-Until "network W/S climbing $Label to y=$Y" {
            $snapshot = Read-JsonSnapshot (Join-Path $roles.host.Root 'view.json')
            if ($null -eq $snapshot) { return $null }
            $miner = Get-Miner $snapshot.Data
            if ([string]::IsNullOrEmpty($miner['ClimbingLadderId'])) { throw "Lost ladder while climbing $Label." }
            $position = $miner['Position']; $delta = $Y - [double]$position['y']
            if ([Math]::Abs($delta) -le $arrivalTolerance) { return $true }
            $axis = Get-ClimbAxis $delta
            Set-Input 'host' 0 $axis 0 0 $false $Label $position
            return $null
        }
    } finally { Set-Neutral 'host' "$Label-stop" }
    $to = (Get-Miner (Get-LiveView 'host').Data)['Position']
    Assert-Check ([Math]::Abs([double]$to['y'] - $Y) -le .20) "$Label reached ladder height with W/S input"
    $evidence.Scenario.Movement.Add([ordered]@{ Role = 'host'; Label = $Label; From = $from; To = $to })
}
function Exit-Ladder {
    # Exit north onto the closest intact rock row; a south exit crosses 1.6m of empty shaft.
    $command = New-Action 'top-unclip' 'unclip'
    Send-Command 'host' $command
    Set-Input 'host' 0 1 0 0 $false 'ladder-north-exit'
    try {
        $result = (Wait-Result 'host' $command.RequestId).Entries[0]
        Assert-Check ($result['Success']) 'Unclip at top succeeded'
        # Keep W held while stepping over the lip. Move-To's proportional slow
        # axis is useful on flat ground but would release most forward input
        # before a heavy carrier has reached this ledge.
        $null = Wait-Until 'heavy carrier steps fully onto the intact north ledge' {
            $p = (Get-Miner (Get-LiveView 'host').Data)['Position']
            if ([double]$p['y'] -lt -.75) { throw 'Heavy carrier fell while holding full forward input onto the north ledge.' }
            return [double]$p['z'] -ge 22.45 -and [double]$p['y'] -gt -.1
        }
        Set-Neutral 'host' 'ledge-reached'
        Move-To 'host' -5 22.8 'north intact row' 0 .75
    } finally { Set-Neutral 'host' 'exit-stop' }
    Assert-Check ([string]::IsNullOrEmpty((Get-Miner (Get-LiveView 'host').Data)['ClimbingLadderId'])) 'Exited ladder with no attachment'
    Move-To 'host' -7 22.8 'west bank outside pit'
    Move-To 'host' -7 18 'west safe return'
}
function Assert-HeldHeavy([bool]$Assisted, [string]$Label) {
    foreach ($role in @('host', 'client')) {
        $snapshot = Wait-View $role { param($view)
            @($view['Items'] | Where-Object { $_['Id'] -ceq $heavyId -and $_['Place'] -eq 1 -and $_['OwnerId'] -ceq $playerIds.host -and
                (($_['AssistantId'] -ceq $playerIds.client) -eq $Assisted) }).Count -eq 1 }
        $item = Find-Entity $snapshot.Data 'Items' $heavyId
        Assert-Check ($item['Quantity'] -eq 1 -and $item['Kind'] -eq 3) "$Label one heavy ore keeps host ownership ($role)"
    }
}
function Measure-Carry([string]$Label, [bool]$Assisted, [double]$Seconds = 2.5) {
    $start = (Get-LiveView 'host').Data
    $p0 = (Get-Miner $start)['Position']; $t0 = [double]$start['OnlineSeconds']
    $sample = $null; $rows = [Collections.Generic.List[object]]::new()
    try {
        Set-Input 'host' 0 1 180 0 $false $Label $p0
        if ($Assisted) { Set-Input 'client' 0 ([double]$rules['TeamCarryMultiplier']) 180 0 $false "$Label-helper" }
        else { Set-Neutral 'client' "$Label-helper-still" }
        $sample = Wait-Until "$Label steady real carry movement" {
            $snapshot = Read-JsonSnapshot (Join-Path $roles.host.Root 'view.json')
            if ($null -eq $snapshot) { return $null }
            $view = $snapshot.Data
            $owner = Find-Entity $view 'Players' $playerIds.host
            $helper = Find-Entity $view 'Players' $playerIds.client
            $item = Find-Entity $view 'Items' $heavyId
            if ($item['OwnerId'] -cne $playerIds.host -or $item['Place'] -ne 1 -or
                (($item['AssistantId'] -ceq $playerIds.client) -ne $Assisted)) { throw "$Label lost carry/assist ownership." }
            if ([Math]::Abs([double]$owner['Position']['y']) -gt .5 -or [Math]::Abs([double]$helper['Position']['y']) -gt .5) { throw "$Label left the flat lane." }
            $rows.Add([ordered]@{ GameSeconds = $view['OnlineSeconds']; Owner = $owner['Position']; Helper = $helper['Position'] })
            if ([double]$view['OnlineSeconds'] -ge $t0 + $Seconds) { return $snapshot }
            return $null
        }
    } finally { Set-Neutral 'host' "$Label-stop"; Set-Neutral 'client' "$Label-stop" }
    $p1 = (Get-Miner $sample.Data)['Position']
    $elapsed = [double]$sample.Data['OnlineSeconds'] - $t0
    $distance = [double]$p0['z'] - [double]$p1['z']; $speed = $distance / $elapsed
    $expected = [double]$rules['WalkSpeed'] * $(if ($Assisted) { [double]$rules['TeamCarryMultiplier'] } else { [double]$rules['SoloCarryMultiplier'] })
    Assert-Check ($speed -ge .70 * $expected -and $speed -le 1.25 * $expected) "$Label speed matches real heavy carry input" "measured=$speed; expected=$expected"
    $evidence.Scenario.SpeedSamples.Add([ordered]@{ Label = $Label; Assisted = $Assisted; Seconds = $elapsed; Distance = $distance; Speed = $speed; Expected = $expected; Positions = $rows })
    Wait-GameSeconds .3
    return $speed
}
function Assert-MiningState($State, [string]$Label) {
    Assert-Check ($State['WorldId'] -ceq $worldId -and @($State['Players']).Count -eq 2) "$Label world and two identities retained"
    foreach ($role in @('host', 'client')) {
        $player = Find-Entity $State 'Players' $playerIds[$role]
        Assert-Check ($player['Balance'] -eq $expectedBalances[$role]) "$Label $role balance retained"
    }
    foreach ($cellId in @($topCell, $lowerCell)) {
        $cell = Find-Entity $State 'Cells' $cellId
        $ladder = Find-Entity $State 'Ladders' "ladder:$cellId"
        Assert-Check ($cell['Dug'] -and $cell['Version'] -eq $persistedVersions[$cellId] -and $ladder['CellId'] -ceq $cellId) "$Label $cellId dug and ladder retained"
    }
    Assert-Check (@($State['Ladders']).Count -eq 2) "$Label retains exactly two connected ladder segments"
    $item = Find-Entity $State 'Items' $heavyId
    Assert-Check ($item['Place'] -eq 7 -and $item['OwnerId'] -ceq $playerIds.host -and $item['Quantity'] -eq 1) "$Label original mined ore remains sold"
    foreach ($requestId in @('b-sell-once', 'b-transfer-once')) {
        $receipts = @($State['Receipts'] | Where-Object { $_['Key'] -ceq ($playerIds.host + ':' + $requestId) })
        Assert-Check ($receipts.Count -eq 1 -and $receipts[0]['Result']['Success']) "$Label retains $requestId receipt"
    }
}
try {
    foreach ($role in @('host', 'client')) {
        $root = Assert-TestPath (Join-Path $runRoot $role)
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Write-AtomicText (Join-Path $root '.goldcamp-test-root') 'GoldCamp B isolated automated test; never a human save.'
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
        $initial[$role] = Wait-View $role { param($view)
            @($view['Players'] | Where-Object { $_['Online'] }).Count -eq 2 -and
            @($view['Cells'] | Where-Object { $_['PitId'] -ceq 'starter' }).Count -eq 144 -and
            @($view['Cells'] | Where-Object { $_['PitId'] -ceq 'advanced' }).Count -eq 80 -and
            @($view['Cells']).Count -eq 224 }
    }
    $playerIds = @{ host = $initial.host.Data['Self']['Id']; client = $initial.client.Data['Self']['Id'] }
    $worldId = [string]$initial.host.Data['WorldId']
    $rules = $initial.host.Data['Rules']
    $initialBalances = @{ host = [long]$initial.host.Data['Self']['Balance']; client = [long]$initial.client.Data['Self']['Balance'] }
    $expectedBalances = @{ host = $initialBalances.host; client = $initialBalances.client }
    Assert-Check ($playerIds.host -cne $playerIds.client -and $roles.host.CurrentProcess.Id -ne $roles.client.CurrentProcess.Id) 'Two distinct persistent identities in two OS processes'
    Assert-Check ($initial.client.Data['WorldId'] -ceq $worldId) 'Two processes share the same host world'
    Assert-Check ($rules['CellSize'] -eq 2 -and $rules['Reach'] -eq 3 -and $rules['StoneWeight'] -eq 48 -and
        $rules['IronWeight'] -eq 27 -and $rules['GoldWeight'] -eq 10 -and $rules['HeavyWeight'] -eq 8 -and $rules['RawWeight'] -eq 7) 'Current B geometry and seed weights match the reviewed test route'
    foreach ($role in @('host', 'client')) { Assert-Projection $initial[$role] $playerIds[$role] "initial-$role" }

    # Stagger both real capsules out of their common spawn, then avoid the sale/repair boxes.
    Move-To 'host' -3 -4 'spawn side lane'
    Move-To 'host' -3 18 'south approach'
    Move-To 'host' -5 19.25 'starter south edge'
    Move-To 'client' -1.6 -4 'client side lane'
    Move-To 'client' -1.6 18 'client south approach'
    Move-To 'client' -3.6 18.5 'client starter overlook'
    $position = (Get-Miner (Get-LiveView 'host').Data)['Position']
    $null = Invoke-Action 'host' (New-Action 'detect-first-cell' 'detect' '' 0 (Aim-At $position -5 -1 21))
    Assert-BlockedMine $lowerCell -5 -3 21 'covered-depth-before-top'
    Mine-Cell $topCell 0 3
    $yield = Wait-View 'host' { param($view) @($view['Items'] | Where-Object { $_['Id'] -ceq $heavyId -and $_['Place'] -eq 0 }).Count -eq 1 }
    $heavy = Find-Entity $yield.Data 'Items' $heavyId
    Assert-Check ($heavy['Kind'] -eq 3 -and $heavy['Quantity'] -eq 1) 'Actual public first-cell yield is one HeavyOre'
    Place-Ladder $topCell
    Attach-Ladder $topCell
    # Pick up inside the shaft before the large collider obstructs full descent.
    Climb-To -.8 'first shaft entry'
    $heavy = Find-Entity (Get-LiveView 'host').Data 'Items' $heavyId
    $null = Invoke-Action 'host' (New-Action 'pit-pickup' 'pickup' $heavyId ([int]$heavy['Version']))
    Assert-HeldHeavy $false 'Pit pickup'
    Climb-To -1.85 'heavy to first floor'
    Climb-To .1 'heavy carried up first ladder'
    Exit-Ladder
    Move-To 'host' -5 18 'flat carry lane'
    $soloSpeed = Measure-Carry 'solo-before-assist' $false
    $position = (Get-Miner (Get-LiveView 'host').Data)['Position']
    Move-To 'client' -3.6 ([double]$position['z']) 'parallel helper lane'
    $heavy = Find-Entity (Get-LiveView 'client').Data 'Items' $heavyId
    $null = Invoke-Action 'client' (New-Action 'join-assist' 'assist' $heavyId ([int]$heavy['Version']))
    Assert-HeldHeavy $true 'Assist joined'
    $teamSpeed = Measure-Carry 'two-player-parallel-assist' $true
    Assert-Check ($teamSpeed -gt 1.45 * $soloSpeed) 'Actual coordinated heavy carry is faster than solo carry'
    $heavy = Find-Entity (Get-LiveView 'client').Data 'Items' $heavyId
    $null = Invoke-Action 'client' (New-Action 'leave-assist' 'leave_assist' $heavyId ([int]$heavy['Version']))
    Assert-HeldHeavy $false 'Assistant left without dropping or stealing'
    $afterSpeed = Measure-Carry 'solo-after-assist' $false 1.4
    Assert-Check ($afterSpeed -lt .8 * $teamSpeed) 'Leaving assist restores solo heavy movement speed'
    $position = (Get-Miner (Get-LiveView 'host').Data)['Position']
    Move-To 'host' -2.5 ([double]$position['z']) 'sale west approach'
    Move-To 'host' -2.5 2 'sale reach'
    $saleView = (Get-LiveView 'host').Data
    $position = (Get-Miner $saleView)['Position']
    $saleDistance = [Math]::Sqrt([double]$position['x'] * [double]$position['x'] + [double]$position['y'] * [double]$position['y'] + [double]$position['z'] * [double]$position['z'])
    Assert-Check ($saleDistance -le 4) 'Mined heavy ore physically carried from shaft to sale range'
    $heavy = Find-Entity $saleView 'Items' $heavyId
    $saleAmount = [long]$heavy['SaleQuote']
    $saleCommand = New-Command 'b-sell-once' 'sell' $heavyId ([int]$heavy['Version'])
    $saleResult = Invoke-Action 'host' $saleCommand
    Assert-Check ($saleAmount -gt 0 -and $saleResult['Amount'] -eq $saleAmount) 'Actual mined heavy ore pays its public quote'
    $expectedBalances.host += $saleAmount
    Send-Command 'host' $saleCommand
    Assert-ResultSame (Wait-Result 'host' 'b-sell-once' 2).Entries[1] $saleResult 'Repeated sale returns its original receipt'
    $transferAmount = [long][Math]::Min(7L, $saleAmount)
    $transfer = New-Command 'b-transfer-once' 'transfer' '' 0 $playerIds.client $transferAmount
    $transferResult = Invoke-Action 'host' $transfer
    Assert-Check ($transferResult['Amount'] -eq $transferAmount) 'One network transfer moved the expected amount'
    $expectedBalances.host -= $transferAmount
    $expectedBalances.client += $transferAmount
    foreach ($role in @('host', 'client')) {
        $view = Wait-View $role { param($data) $data['Self']['Balance'] -eq $expectedBalances[$role] -and @($data['Items'] | Where-Object { $_['Id'] -ceq $heavyId }).Count -eq 0 }
        Assert-Projection $view $playerIds[$role] "sold-$role"
    }
    Assert-Check (($expectedBalances.host + $expectedBalances.client) -eq ($initialBalances.host + $initialBalances.client + $saleAmount)) 'Transfer preserves total money; duplicate sale adds nothing'

    # Return through the solid surface to excavate the lower level with free hands.
    Move-To 'host' -1.6 5 'return from sale'
    Move-To 'host' -1.6 18 'return toward starter'
    Move-To 'host' -5 19.25 'return to first ladder'
    Attach-Ladder $topCell
    Climb-To -1.85 'empty-handed descent for lower layer'
    $null = Invoke-Action 'host' (New-Action 'bottom-unclip' 'unclip')
    Wait-GameSeconds .4
    Assert-BlockedMine 'starter:0:2:0' -5 -5 21 'covered-deeper-layer'
    Mine-Cell $lowerCell 1 4
    Wait-GameSeconds .7
    Place-Ladder $lowerCell
    $null = Wait-View 'client' { param($view) @($view['Ladders']).Count -eq 2 -and
        @($view['Cells'] | Where-Object { $_['Id'] -in @($topCell, $lowerCell) -and $_['Dug'] }).Count -eq 2 }
    Attach-Ladder $lowerCell
    Climb-To -3.78 'lower supported ladder descent'
    $null = Invoke-Action 'host' (New-Action 'lower-floor-unclip' 'unclip')
    Wait-GameSeconds .35
    Assert-BlockedMine 'starter:0:0:2' -5 -1 25 'covered-overhead-row'
    Attach-Ladder $lowerCell
    Climb-To .1 'two-segment ascent'
    Exit-Ladder
    $persistedVersions = @{}
    foreach ($cellId in @($topCell, $lowerCell)) {
        $persistedVersions[$cellId] = (Find-Entity (Get-LiveView 'host').Data 'Cells' $cellId)['Version']
    }
    foreach ($role in @('host', 'client')) { Assert-Projection (Get-LiveView $role) $playerIds[$role] "before-restart-$role" }
    Stop-Player 'client'
    Stop-Player 'host'
    Assert-MiningState (Read-World $worldId) 'Before restart'
    foreach ($role in @('host', 'client')) { Archive-ControlFiles $role }

    Start-Player 'host' 'reopened' $false
    $null = Get-LiveView 'host'
    Start-Player 'client' 'reopened' $false
    foreach ($role in @('host', 'client')) {
        $view = Wait-View $role { param($data) $data['WorldId'] -ceq $worldId -and $data['Self']['Id'] -ceq $playerIds[$role] -and
            $data['Self']['Balance'] -eq $expectedBalances[$role] -and @($data['Players'] | Where-Object { $_['Online'] }).Count -eq 2 }
        Assert-Projection $view $playerIds[$role] "reopened-$role"
        foreach ($cellId in @($topCell, $lowerCell)) {
            $cell = Find-Entity $view.Data 'Cells' $cellId
            $ladder = Find-Entity $view.Data 'Ladders' "ladder:$cellId"
            Assert-Check ($cell['Dug'] -and $cell['Version'] -eq $persistedVersions[$cellId] -and $ladder['CellId'] -ceq $cellId) "Reopened $role sees persistent $cellId excavation and ladder"
        }
        Assert-Check (@($view.Data['Items'] | Where-Object { $_['Id'] -ceq $heavyId }).Count -eq 0) "Reopened $role does not resurrect sold mining yield"
    }
    Send-Command 'host' $saleCommand
    Assert-ResultSame (Wait-Result 'host' 'b-sell-once').Entries[0] $saleResult 'Mined ore sale receipt survives process restart'
    foreach ($role in @('host', 'client')) {
        $null = Wait-View $role { param($data) $data['Self']['Balance'] -eq $expectedBalances[$role] }
    }
    Stop-Player 'client'
    Stop-Player 'host'
    Assert-MiningState (Read-World $worldId) 'After restart'
    $evidence.Scenario['WorldId'] = $worldId
    $evidence.Scenario['PlayerIds'] = $playerIds
    $evidence.Scenario['MinedHeavyId'] = $heavyId
    $evidence.Scenario['SaleAmount'] = $saleAmount
    $evidence.Scenario['TransferAmount'] = $transferAmount
    $evidence.Scenario['FinalBalances'] = $expectedBalances
    $evidence.Scenario['DugCellVersions'] = $persistedVersions
    $evidence.Success = $true
} catch {
    $evidence.Error = $_.Exception.Message
    $evidence.Scenario['FailureLine'] = $_.InvocationInfo.ScriptLineNumber
} finally {
    # Stop only handles created by this script. Never identify processes by name.
    foreach ($entry in $processes) {
        if (-not $entry.Process.HasExited) {
            $entry.ExpectedRunning = $false
            try { Set-Neutral $entry.Role 'failure-neutral'; Send-Command $entry.Role (New-Command 'b-cleanup-shutdown' 'shutdown') } catch { }
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
if (-not $evidence.Success) { throw "Demo mining validation failed. Evidence: $evidencePath" }
