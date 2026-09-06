#requires -Version 7.0
<#
.SYNOPSIS
Exercises checkpoint A through two real GoldCamp player processes and FishNet.
.DESCRIPTION
Creates fresh, marked test roots under .local/TestRuns; never accepts a save path.
CampTestDriver consumes inbox.jsonl. commands.jsonl is an identical audit copy.
The host is reopened without --gc-new and both persistent identities reconnect.
FullMineSnapshot adds a development-only fully dug two-pit fixture, then checks that
both projections and the reopened camp retain every cell, ladder and original yield.
Snapshot size estimates are diagnostic data, not measured Unity wire payloads.
SnapshotReaderContention deliberately blocks one diagnostic view writer and checks
that real gameplay, peer updates and subsequent snapshot export remain functional.
This is automated loopback validation, not human input, LAN, Steam, or voice testing.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 300)] [int]$TimeoutSeconds = 60,
    [switch]$WithGraphics,
    [switch]$FullMineSnapshot,
    [switch]$SnapshotReaderContention
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$workspaceRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$buildPath = Join-Path $workspaceRoot 'Builds\DemoPrototype\Windows\GoldCamp.exe'
if (-not (Test-Path -LiteralPath $buildPath -PathType Leaf)) {
    throw "Build the Development player first: $buildPath"
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
    Suite = $(if ($FullMineSnapshot) { 'A-FishNet-full-mine-snapshot-two-process-loopback' } else { 'A-FishNet-two-process-loopback' })
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
            } catch [IO.IOException] {
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
        if ($NewWorld) { $arguments.Add('--gc-new'); $arguments.Add('--gc-test-fixture') }
        if ($NewWorld -and $FullMineSnapshot) { $arguments.Add('--gc-test-capacity') }
    } else {
        $arguments.Add('--gc-connect'); $arguments.Add('127.0.0.1')
    }
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
    Send-Command $Role (New-Command "a-shutdown-$Role-$($entry.Phase)" 'shutdown')
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
    foreach ($file in @('view.json', 'results.jsonl', 'inbox.jsonl', 'commands.jsonl', 'process.json')) {
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
    Assert-Check $valid "$Label projection privacy" 'Only Self may contain Balance; no host-only fields or fixture secret.'
    Write-AtomicText (Join-Path $runRoot ("view-$Label.json")) $Snapshot.Raw
    if ($FullMineSnapshot) {
        Assert-CapacityData $Snapshot.Data $Label
        Measure-CapacitySnapshot $Snapshot $Label
    }
}

function New-CapacityBaseline($Data) {
    return [ordered]@{
        CellIds = [string[]]@($Data['Cells'] | ForEach-Object { [string]$_['Id'] } | Sort-Object)
        LadderIds = [string[]]@($Data['Ladders'] | ForEach-Object { [string]$_['Id'] } | Sort-Object)
        YieldIds = [string[]]@($Data['Items'] | Where-Object { [string]$_['Id'] -cmatch '^yield:' } |
            ForEach-Object { [string]$_['Id'] } | Sort-Object)
    }
}

function Assert-CapacityData($Data, [string]$Label) {
    # Inspect only public geometry/ownership here. This helper also checks the stopped
    # authority state without copying hidden mineral contents into diagnostics.
    $cells = @($Data['Cells'])
    $ladders = @($Data['Ladders'])
    $items = @($Data['Items'])
    $yields = @($items | Where-Object { [string]$_['Id'] -cmatch '^yield:' })
    Assert-Check ($cells.Count -eq 224 -and
        @($cells | Where-Object { $_['Dug'] -ne $true -or [double]$_['Health'] -ne 0 }).Count -eq 0) "$Label retains all 224 dug cells with zero health"
    Assert-Check (@($cells | Where-Object { $_['PitId'] -ceq 'starter' }).Count -eq 144 -and
        @($cells | Where-Object { $_['PitId'] -ceq 'advanced' }).Count -eq 80) "$Label retains both complete mine regions"

    $cellIds = [string[]]@($cells | ForEach-Object { [string]$_['Id'] })
    $cellSet = [Collections.Generic.HashSet[string]]::new($cellIds, [StringComparer]::Ordinal)
    Assert-Check ($cellSet.Count -eq 224 -and -not $cellSet.Contains('') -and
        $cellSet.SetEquals([string[]]$capacityBaseline.CellIds)) "$Label retains the original unique cell IDs"
    $ladderIds = [string[]]@($ladders | ForEach-Object { [string]$_['Id'] })
    $ladderSet = [Collections.Generic.HashSet[string]]::new($ladderIds, [StringComparer]::Ordinal)
    $ladderCells = [Collections.Generic.HashSet[string]]::new(
        [string[]]@($ladders | ForEach-Object { [string]$_['CellId'] }), [StringComparer]::Ordinal)
    Assert-Check ($ladders.Count -eq 224 -and $ladderSet.Count -eq 224 -and -not $ladderSet.Contains('') -and
        $ladderCells.SetEquals($cellSet) -and $ladderSet.SetEquals([string[]]$capacityBaseline.LadderIds)) "$Label retains exactly one original ladder per dug cell"

    $cellMap = [Collections.Generic.Dictionary[string, object]]::new([StringComparer]::Ordinal)
    foreach ($cell in $cells) { $cellMap.Add([string]$cell['Id'], $cell) }
    $supported = $true
    foreach ($ladder in $ladders) {
        $cell = $cellMap[[string]$ladder['CellId']]
        if ($ladder['PitId'] -cne $cell['PitId'] -or $ladder['X'] -ne $cell['X'] -or
            $ladder['Depth'] -ne $cell['Depth'] -or $ladder['Z'] -ne $cell['Z']) { $supported = $false; break }
        if ([int]$ladder['Depth'] -gt 0) {
            $above = '{0}:{1}:{2}:{3}' -f $ladder['PitId'], $ladder['X'], ([int]$ladder['Depth'] - 1), $ladder['Z']
            if (-not $ladderCells.Contains($above)) { $supported = $false; break }
        }
    }
    Assert-Check $supported "$Label ladder geometry and continuous upper support remain intact"

    $itemIds = [string[]]@($items | ForEach-Object { [string]$_['Id'] })
    $itemSet = [Collections.Generic.HashSet[string]]::new($itemIds, [StringComparer]::Ordinal)
    Assert-Check ($itemSet.Count -eq $items.Count -and -not $itemSet.Contains('')) "$Label contains no duplicate item IDs"
    $yieldIds = [string[]]@($yields | ForEach-Object { [string]$_['Id'] })
    $yieldSet = [Collections.Generic.HashSet[string]]::new($yieldIds, [StringComparer]::Ordinal)
    Assert-Check ($yields.Count -ge 224 -and $yieldSet.Count -eq $yields.Count -and
        $yieldSet.SetEquals([string[]]$capacityBaseline.YieldIds)) "$Label retains every original public yield" "count=$($yields.Count)"
    Assert-Check (@($yields | Where-Object { $_['Place'] -ne 0 -or [int]$_['Quantity'] -lt 1 -or
        -not [string]::IsNullOrEmpty([string]$_['OwnerId']) }).Count -eq 0) "$Label original yields remain unowned ground items"
    $yieldCells = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    $validYieldIds = $true
    foreach ($yieldId in $yieldIds) {
        if ($yieldId -cmatch '^yield:((?:starter|advanced):[0-9]+:[0-9]+:[0-9]+):[0-9]+$' -and $cellSet.Contains($Matches[1])) {
            $null = $yieldCells.Add($Matches[1])
        } else { $validYieldIds = $false; break }
    }
    Assert-Check ($validYieldIds -and $yieldCells.SetEquals($cellSet)) "$Label has original yields for every dug cell"

    [long]$contributedCoins = 0
    [long]$contributedIron = 0
    foreach ($contribution in @($Data['Contributions'])) {
        $contributedCoins += [long]$contribution['Coins']
        $contributedIron += [long]$contribution['Iron']
    }
    Assert-Check ($Data['RoadUnlocked'] -eq $true -and $Data['RoadCoinsPaid'] -eq $Data['RoadRequiredCoins'] -and
        $Data['RoadIronPaid'] -eq $Data['RoadRequiredIron'] -and $contributedCoins -eq $Data['RoadCoinsPaid'] -and
        $contributedIron -eq $Data['RoadIronPaid']) "$Label retains the completed road and matching public contributions"
}

function Measure-CapacitySnapshot($Snapshot, [string]$Label) {
    # The view text was emitted by CampTestDriver. Wrapping that exact text follows
    # CampPacket's two fields, but PowerShell JSON escaping can differ from JsonUtility.
    $estimatedPacket = ConvertTo-Json -InputObject ([ordered]@{ Type = 'view'; Payload = $Snapshot.Raw }) -Compress -Depth 4
    $estimatedBytes = $utf8.GetByteCount($estimatedPacket)
    $capacitySizes.Add([ordered]@{
        Label = $Label
        ViewJsonUtf8Bytes = $utf8.GetByteCount($Snapshot.Raw)
        PowerShellCampPacketUtf8Bytes = $estimatedBytes
        HistoricalLimitBytes = 128 * 1024
        EstimateExceedsHistoricalLimit = $estimatedBytes -gt (128 * 1024)
        Measurement = 'Client view.json plus a PowerShell JSON CampPacket wrapper; not Unity wire bytes or compressed traffic.'
    })
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

function Assert-SavedState($State, [string]$Label) {
    Assert-Check ($State['WorldId'] -ceq $worldId) "$Label retains world ID"
    Assert-Check (@($State['Players']).Count -eq 2) "$Label retains exactly two identities"
    foreach ($role in @('host', 'client')) {
        $players = @($State['Players'] | Where-Object { $_['Id'] -eq $playerIds[$role] })
        Assert-Check ($players.Count -eq 1 -and $players[0]['Balance'] -eq $expectedBalances[$role]) "$Label retains $role identity and balance"
    }
    $items = @($State['Items'] | Where-Object { $_['Id'] -eq 'a-ore' })
    # CampState.ItemPlace: Ground=0, Hand=1, ... Escrow=6, Sold=7.
    Assert-Check ($items.Count -eq 1 -and $items[0]['Place'] -eq 7 -and $items[0]['OwnerId'] -eq $playerIds[$winnerRole] -and
        $items[0]['Quantity'] -eq 1 -and $items[0]['Version'] -eq ([int]$ore['Version'] + 2)) "$Label retains one sold ore"
    foreach ($request in @('a-sell-once', 'a-transfer-once')) {
        $receipts = @($State['Receipts'] | Where-Object { $_['Key'] -ceq ($playerIds[$winnerRole] + ':' + $request) })
        Assert-Check ($receipts.Count -eq 1 -and $receipts[0]['Result']['Success'] -eq $true) "$Label retains one $request receipt"
        if ($request -eq 'a-sell-once') { Assert-ResultSame $receipts[0]['Result'] $saleResult "$Label retains original sale result" }
    }
    if ($FullMineSnapshot) { Assert-CapacityData $State $Label }
}

try {
    foreach ($role in @('host', 'client')) {
        $root = Assert-TestPath (Join-Path $runRoot $role)
        [IO.Directory]::CreateDirectory($root) | Out-Null
        Write-AtomicText (Join-Path $root '.goldcamp-test-root') 'GoldCamp isolated automated test; not a human save.'
        $roles[$role] = @{ Root = $root; InboxLines = [Collections.Generic.List[string]]::new(); CurrentProcess = $null }
    }
    $reservation = [Net.Sockets.UdpClient]::new([Net.Sockets.AddressFamily]::InterNetwork)
    try {
        $reservation.Client.ExclusiveAddressUse = $true
        $reservation.Client.Bind([Net.IPEndPoint]::new([Net.IPAddress]::Loopback, 0))
        $evidence.Port = ([Net.IPEndPoint]$reservation.Client.LocalEndPoint).Port
    } finally { $reservation.Dispose() }

    Start-Player 'host' 'first' $true
    $null = Wait-View 'host' { param($view) $null -ne $view['Self']['Id'] }
    Start-Player 'client' 'first' $false
    $initialViews = @{}
    foreach ($role in @('host', 'client')) {
        $initialViews[$role] = Wait-View $role { param($view) @($view['Players']).Count -eq 2 -and @($view['Items'] | Where-Object { $_['Id'] -eq 'a-ore' }).Count -eq 1 }
    }
    $worldId = [string]$initialViews['host'].Data['WorldId']
    $playerIds = @{ host = [string]$initialViews['host'].Data['Self']['Id']; client = [string]$initialViews['client'].Data['Self']['Id'] }
    Assert-Check ($playerIds.host -match '^[0-9a-f]{32}$' -and $playerIds.client -match '^[0-9a-f]{32}$' -and $playerIds.host -cne $playerIds.client) 'Two separate persistent player identities'
    Assert-Check ($initialViews['client'].Data['WorldId'] -ceq $worldId) 'Both processes joined the same world'
    Assert-Check ($roles.host.CurrentProcess.Id -ne $roles.client.CurrentProcess.Id) 'Host and client are distinct OS processes'
    if ($SnapshotReaderContention) {
        $path = Assert-TestPath (Join-Path $roles.host.Root 'view.json')
        $held = $null
        try {
            for ($attempt = 0; $attempt -lt 40 -and $null -eq $held; $attempt++) {
                try { $held = [IO.File]::Open($path, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read) }
                catch [IO.IOException] { Start-Sleep -Milliseconds 25 }
            }
            Assert-Check ($null -ne $held) 'Snapshot observer holds a real reader that denies writes'
            $beforeWrite = [IO.File]::GetLastWriteTimeUtc($path)
            Send-Command 'host' (New-Command 'a-observer-peace' 'pvp' '' 0 '' 0)
            $changed = (Wait-Result 'host' 'a-observer-peace').Entries[0]
            Assert-Check ($changed['Success']) 'Gameplay transaction succeeds while diagnostics export is locked'
            $peerView = Wait-View 'client' { param($view) $view['PvpEnabled'] -eq $false }
            Assert-Check ($peerView.Data['PvpEnabled'] -eq $false) 'Other client receives the gameplay update while host diagnostics is locked'
            Start-Sleep -Milliseconds 500
            Assert-Check ([IO.File]::GetLastWriteTimeUtc($path) -eq $beforeWrite) 'Locked host diagnostics was not rewritten'
        } finally { if ($null -ne $held) { $held.Dispose() } }
        $hostView = Wait-View 'host' { param($view) $view['PvpEnabled'] -eq $false }
        Assert-Check ([IO.File]::GetLastWriteTimeUtc($path) -gt $beforeWrite) 'Latest complete snapshot resumes after the reader releases the file'
        Send-Command 'host' (New-Command 'a-observer-restore-pvp' 'pvp' '' 0 '' 1)
        $restored = (Wait-Result 'host' 'a-observer-restore-pvp').Entries[0]
        Assert-Check ($restored['Success']) 'Host restores original camp rule after diagnostic contention'
        $null = Wait-View 'host' { param($view) $view['PvpEnabled'] -eq $true }
        $null = Wait-View 'client' { param($view) $view['PvpEnabled'] -eq $true }
        $evidence.Scenario['SnapshotReaderContention'] = 'Actual read handle denies diagnostic writes; gameplay and peer projection continue, latest host view recovers after release.'
    }
    if ($FullMineSnapshot) {
        $capacityBaseline = New-CapacityBaseline $initialViews['host'].Data
        $capacitySizes = [Collections.Generic.List[object]]::new()
        $evidence.Scenario['FullMineSnapshot'] = [ordered]@{
            Fixture = 'Development-only --gc-test-capacity on the first new host; normal resource progression is not tested by this fixture.'
            ExpectedCells = 224; ExpectedLadders = 224
            OriginalCellIds = $capacityBaseline.CellIds
            OriginalLadderIds = $capacityBaseline.LadderIds
            PublicOriginalYieldCount = $capacityBaseline.YieldIds.Count
            OriginalYieldIds = $capacityBaseline.YieldIds
            SnapshotSizes = $capacitySizes
        }
    }
    $initialBalances = @{}
    foreach ($role in @('host', 'client')) {
        $initialBalances[$role] = [long]$initialViews[$role].Data['Self']['Balance']
        Assert-Projection $initialViews[$role] $playerIds[$role] "initial-$role"
    }
    $ore = @($initialViews['host'].Data['Items'] | Where-Object { $_['Id'] -eq 'a-ore' })[0]
    $clientOre = @($initialViews['client'].Data['Items'] | Where-Object { $_['Id'] -eq 'a-ore' })[0]
    Assert-Check ($ore['Place'] -eq 0 -and $clientOre['Place'] -eq 0 -and $ore['Version'] -eq $clientOre['Version']) 'Contenders observe the same ground ore version'
    $pickup = New-Command 'a-pickup' 'pickup' 'a-ore' ([int]$ore['Version'])
    $dispatch = [Diagnostics.Stopwatch]::StartNew()
    Send-Command 'host' $pickup
    Send-Command 'client' $pickup
    $dispatch.Stop()
    $evidence.Scenario['PickupDispatchMilliseconds'] = $dispatch.Elapsed.TotalMilliseconds
    $evidence.Scenario['PickupExpectedVersion'] = $ore['Version']
    # Both requests are outstanding before either result is awaited.
    $pickupResults = @{}
    foreach ($role in @('host', 'client')) { $pickupResults[$role] = (Wait-Result $role 'a-pickup').Entries[0] }
    $winners = @(@('host', 'client') | Where-Object { $pickupResults[$_]['Success'] -eq $true })
    Assert-Check ($winners.Count -eq 1) 'Concurrent same-version pickup has exactly one winner'
    $winnerRole = $winners[0]
    $loserRole = if ($winnerRole -eq 'host') { 'client' } else { 'host' }
    Assert-Check ($pickupResults[$loserRole]['Code'] -eq 'stale') 'Losing same-version pickup is rejected as stale'
    foreach ($role in @('host', 'client')) {
        $heldView = Wait-View $role { param($view) @($view['Items'] | Where-Object { $_['Id'] -eq 'a-ore' -and $_['Place'] -eq 1 -and $_['OwnerId'] -eq $playerIds[$winnerRole] }).Count -eq 1 }
        Assert-Projection $heldView $playerIds[$role] "held-$role"
    }
    $heldOre = @($heldView.Data['Items'] | Where-Object { $_['Id'] -eq 'a-ore' })[0]
    $saleAmount = [long]$heldOre['SaleQuote']
    Assert-Check ($saleAmount -gt 0) 'Fixture ore has a positive sale quote'
    $sale = New-Command 'a-sell-once' 'sell' 'a-ore' ([int]$heldOre['Version'])
    Send-Command $winnerRole $sale
    $saleResult = (Wait-Result $winnerRole 'a-sell-once').Entries[0]
    Assert-Check ($saleResult['Success'] -eq $true -and $saleResult['Amount'] -eq $saleAmount) 'Winner sold the ore for its quoted price'
    $expectedBalances = @{ host = $initialBalances.host; client = $initialBalances.client }
    $expectedBalances[$winnerRole] += $saleAmount
    Send-Command $winnerRole $sale
    $duplicate = (Wait-Result $winnerRole 'a-sell-once' 2).Entries[1]
    Assert-ResultSame $duplicate $saleResult 'Duplicate sale returns the same receipt'
    $saleBalances = @{}
    foreach ($role in @('host', 'client')) {
        $view = Wait-View $role { param($data) $data['Self']['Balance'] -eq $expectedBalances[$role] -and @($data['Items'] | Where-Object { $_['Id'] -eq 'a-ore' }).Count -eq 0 }
        $saleBalances[$role] = [long]$view.Data['Self']['Balance']
        Assert-Projection $view $playerIds[$role] "sold-$role"
    }
    Assert-Check ($saleBalances[$winnerRole] -eq ($initialBalances[$winnerRole] + $saleAmount) -and
        $saleBalances[$loserRole] -eq $initialBalances[$loserRole]) 'Duplicate sale did not add a second payout' "Sale amount: $saleAmount"
    $transferAmount = [long][Math]::Min(7L, $saleAmount)
    Send-Command $winnerRole (New-Command 'a-transfer-once' 'transfer' '' 0 $playerIds[$loserRole] $transferAmount)
    $transferResult = (Wait-Result $winnerRole 'a-transfer-once').Entries[0]
    Assert-Check ($transferResult['Success'] -eq $true -and $transferResult['Amount'] -eq $transferAmount) 'One transfer succeeded'
    $expectedBalances[$winnerRole] -= $transferAmount
    $expectedBalances[$loserRole] += $transferAmount
    $transferredBalances = @{}
    foreach ($role in @('host', 'client')) {
        $view = Wait-View $role { param($data) $data['Self']['Balance'] -eq $expectedBalances[$role] }
        $transferredBalances[$role] = [long]$view.Data['Self']['Balance']
        Assert-Projection $view $playerIds[$role] "transferred-$role"
    }
    Assert-Check (($transferredBalances.host + $transferredBalances.client) -eq ($initialBalances.host + $initialBalances.client + $saleAmount)) 'Transfer preserves combined money'
    Stop-Player 'client'
    Stop-Player 'host'
    $saved = Read-World $worldId
    Assert-SavedState $saved 'Before restart'
    $fixture = @($saved['Items'] | Where-Object { $_['Id'] -eq 'a-ore' })[0]
    Assert-Check ($fixture['HiddenValue'] -eq 12345 -and $fixture['CoreKind'] -ceq 'host-secret-test') 'Host-only fixture secret exists in the saved authority state'
    foreach ($role in @('host', 'client')) { Archive-ControlFiles $role }

    Start-Player 'host' 'reopened' $false
    $null = Wait-View 'host' { param($data) $data['Self']['Id'] -ceq $playerIds.host }
    Start-Player 'client' 'reopened' $false
    foreach ($role in @('host', 'client')) {
        $view = Wait-View $role { param($data) $data['WorldId'] -ceq $worldId -and $data['Self']['Id'] -ceq $playerIds[$role] -and $data['Self']['Balance'] -eq $expectedBalances[$role] }
        Assert-Projection $view $playerIds[$role] "reopened-$role"
        Assert-Check (@($view.Data['Items'] | Where-Object { $_['Id'] -eq 'a-ore' }).Count -eq 0) "Reopened $role projection does not resurrect sold ore"
    }
    Send-Command $winnerRole $sale
    $replayed = (Wait-Result $winnerRole 'a-sell-once').Entries[0]
    Assert-ResultSame $replayed $saleResult 'Sale receipt remains idempotent after process restart'
    foreach ($role in @('host', 'client')) {
        $view = Wait-View $role { param($data) $data['Self']['Balance'] -eq $expectedBalances[$role] }
        Assert-Projection $view $playerIds[$role] "final-$role"
    }
    Stop-Player 'client'
    Stop-Player 'host'
    Assert-SavedState (Read-World $worldId) 'After restart'
    $evidence.Scenario['WorldId'] = $worldId
    $evidence.Scenario['PlayerIds'] = $playerIds
    $evidence.Scenario['WinnerRole'] = $winnerRole
    $evidence.Scenario['SaleAmount'] = $saleAmount
    $evidence.Scenario['TransferAmount'] = $transferAmount
    $evidence.Scenario['InitialBalances'] = $initialBalances
    $evidence.Scenario['FinalBalances'] = $transferredBalances
    $evidence.Scenario['SaleResult'] = $saleResult
    if ($SnapshotReaderContention) {
        foreach ($entry in $processes) {
            $errors = @(Select-String -LiteralPath $entry.LogPath -Pattern 'IOException|NullReferenceException|MissingReferenceException|InvalidOperationException|IndexOutOfRangeException')
            Assert-Check ($errors.Count -eq 0) "Diagnostic contention has no escaping exceptions ($($entry.Role)/$($entry.Phase))"
        }
    }
    $evidence.Success = $true
} catch {
    $evidence.Error = $_.Exception.Message
} finally {
    # Gracefully stop only player handles created by this invocation, even after an assertion fails.
    foreach ($entry in $processes) {
        if (-not $entry.Process.HasExited) {
            $entry.ExpectedRunning = $false
            try { Send-Command $entry.Role (New-Command 'a-cleanup-shutdown' 'shutdown') } catch { }
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
    Write-AtomicText $evidencePath (ConvertTo-Json -InputObject $evidence -Depth 30)
}

[pscustomobject]@{ Success = $evidence.Success; EvidencePath = $evidencePath; RunRoot = $runRoot; Checks = $checks.Count; Port = $evidence.Port }
if (-not $evidence.Success) { throw "Demo network validation failed. Evidence: $evidencePath" }
