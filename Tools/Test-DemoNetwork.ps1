#requires -Version 7.0
<#
.SYNOPSIS
Exercises checkpoint A through two real GoldCamp player processes and FishNet.
.DESCRIPTION
Creates fresh, marked test roots under .local/TestRuns; never accepts a save path.
CampTestDriver consumes inbox.jsonl. commands.jsonl is an identical audit copy.
The host is reopened without --gc-new and both persistent identities reconnect.
This is automated loopback validation, not human input, LAN, Steam, or voice testing.
#>
[CmdletBinding()]
param(
    [ValidateRange(10, 300)] [int]$TimeoutSeconds = 60,
    [switch]$WithGraphics
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
    Suite = 'A-FishNet-two-process-loopback'
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
