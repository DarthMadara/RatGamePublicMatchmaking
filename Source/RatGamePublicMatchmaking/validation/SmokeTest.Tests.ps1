$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
Import-Module (Join-Path $PSScriptRoot 'SmokeTestSupport.psm1') -Force
$passed = 0
function Check([bool]$Condition, [string]$Description) {
    if (!$Condition) { throw ('FAIL: ' + $Description) }
    $script:passed++; Write-Output ('PASS: ' + $Description)
}
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$testParent = Join-Path $projectRoot '.cache'
$fixture = Join-Path $testParent ('smoke-tests-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixture -Force | Out-Null
try {
    $empty = Get-SmokeObservation $null
    Check (!$empty.PluginInitialized -and !$empty.SteamReady -and !$empty.WaitingLobby) 'empty/truncated log is a poll miss, not an exception'
    $header = '[Message: BepInEx] BepInEx 5.4.23.5'
    $complete = @($header,
        'Rat Game Public Matchmaking 0.2.0-dev.1 by Madara loaded.',
        'Chainloader startup complete',
        'clientReady=True; appsInterfaceReady=True; build=26403247; reason=ready',
        'Host lobby created.',
        'Host lifecycle=waiting; Public Hosting requested=False') -join "`n"
    $ready = Get-SmokeObservation $complete
    Check ($ready.PluginInitialized -and $ready.ChainloaderComplete) 'post-patching plugin and chainloader startup detected'
    Check ($ready.SteamReady -and $ready.SteamBuild -eq '26403247') 'Steam readiness requires client, Apps and positive build'
    Check ($ready.HostLobbyCreated -and $ready.WaitingLobby) 'created lobby and waiting host are distinct recorded events'
    $beforeReady = Get-SmokeObservation ($header + "`nRat Game Public Matchmaking 0.2.0-dev.1 by Madara loaded.`nclientReady=False; appsInterfaceReady=False; build=unavailable")
    Check ($beforeReady.PluginInitialized -and !$beforeReady.SteamReady) 'plugin initialization does not imply Steam readiness'
    $fresh = Get-SmokeObservation ($complete + "`n" + $header + "`nRat Game Public Matchmaking 0.1.0 loaded.")
    Check (!$fresh.PluginInitialized -and !$fresh.SteamReady -and !$fresh.WaitingLobby) 'previous successful run cannot satisfy current-run checks'
    Check (!(Get-SmokeObservation 'clientReady=True; appsInterfaceReady=False; build=26403247; reason=ready').SteamReady) 'partial Steam readiness fails closed'
    Check (!(Get-SmokeObservation 'clientReady=True; appsInterfaceReady=True; build=0; reason=ready').SteamReady) 'unknown Steam build fails readiness'
    Check ((Get-SmokeObservation 'SteamApi_Init returned false').SteamInitFailed) 'SDK initialization failure is recorded separately'

    $state = [pscustomobject]@{PrimaryErrors=@();CleanupErrors=@()}
    try { throw 'original launch failure' } catch { $null = Add-SmokeError $state Primary Launch $_ }
    try { throw 'DLL still mapped' } catch { $null = Add-SmokeError $state Cleanup Restore $_ }
    Check ($state.PrimaryErrors.Count -eq 1 -and $state.PrimaryErrors[0].Message -eq 'original launch failure') 'cleanup failure preserves original primary exception'
    Check ($state.CleanupErrors.Count -eq 1 -and $state.CleanupErrors[0].Message -eq 'DLL still mapped') 'cleanup exception has its own category and stage'
    $roundtrip = $state | ConvertTo-Json -Depth 8 | ConvertFrom-Json
    Check ($roundtrip.PrimaryErrors[0].Stage -eq 'Launch' -and $roundtrip.CleanupErrors[0].Stage -eq 'Restore') 'independent error records survive JSON persistence'

    $emptyFile = Join-Path $fixture 'empty.log'
    [IO.File]::WriteAllText($emptyFile, '')
    Check ((Get-SmokeText $emptyFile) -eq '') 'an empty on-disk log never causes a null Contains call'
    Check ((Get-SmokeText (Join-Path $fixture 'not-created.log')) -eq '') 'missing log during startup is tolerated'
    $liveLog = Join-Path $fixture 'live.log'
    [IO.File]::WriteAllText($liveLog, 'current live log')
    $expectedLiveHash = Get-SmokeHash $liveLog
    $writer = [IO.File]::Open($liveLog, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::Read)
    try {
        Check ((Get-SmokeText $liveLog) -eq 'current live log') 'current log is readable while BepInEx-style writer remains open'
        Check ((Get-SmokeHash $liveLog) -eq $expectedLiveHash) 'live-log hash shares access with an existing logger writer'
    }
    finally { $writer.Dispose() }
    $source = Join-Path $fixture 'source.dll'
    $destination = Join-Path $fixture 'destination.dll'
    [IO.File]::WriteAllBytes($source, [byte[]](1,2,3,4,5))
    [IO.File]::WriteAllBytes($destination, [byte[]](9,8,7))
    $originalHash = Get-SmokeHash $destination
    $blocked = $false
    try { Set-SmokeFile $source $destination { throw 'game still running' } } catch { $blocked = $_.Exception.Message -eq 'game still running' }
    Check ($blocked -and (Get-SmokeHash $destination) -eq $originalHash) 'running-game guard prevents any destination write'
    $script:guardCalls = 0
    $blocked = $false
    try { Set-SmokeFile $source $destination { $script:guardCalls++; if ($script:guardCalls -eq 2) { throw 'game started during staging' } } } catch { $blocked = $true }
    Check ($blocked -and $script:guardCalls -eq 2 -and (Get-SmokeHash $destination) -eq $originalHash) 'guard is rechecked immediately before atomic replacement'
    Set-SmokeFile $source $destination { }
    Check ((Get-SmokeHash $destination) -eq (Get-SmokeHash $source)) 'closed-game replacement verifies exact bytes'
    $lock = [IO.File]::Open($destination, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
    try {
        $blocked = $false
        try { Set-SmokeFile $source $destination { } } catch { $blocked = $true }
        Check $blocked 'mapped/locked destination produces a retained file-operation failure'
    }
    finally { $lock.Dispose() }
    Check (@(Get-ChildItem -LiteralPath $fixture -Filter '*.tmp').Count -eq 0) 'failed replacement removes its temporary file'
    $backup = Join-Path $fixture 'backup.cfg'
    [IO.File]::WriteAllBytes($backup, [byte[]](239,187,191,65,13,10,66))
    Set-SmokeFile $backup $destination { }
    Check ((Get-SmokeHash $backup) -eq (Get-SmokeHash $destination)) 'restoration preserves BOM and newline bytes exactly'
    $tokens = $null; $parseErrors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'SmokeTest.ps1'), [ref]$tokens, [ref]$parseErrors)
    Check (@($parseErrors).Count -eq 0) 'harness parses under the installed PowerShell version'
    $scriptText = [IO.File]::ReadAllText((Join-Path $PSScriptRoot 'SmokeTest.ps1'))
    Check (!$scriptText.Contains('Start-Process -FilePath $gameExe') -and !$scriptText.Contains('[switch]$Direct')) 'harness exposes no direct-executable launch mode'
    Check (!$scriptText.Contains('Stop-Process') -and !$scriptText.Contains('CloseMainWindow')) 'harness never kills or closes a user game session'
    Write-Output ($passed.ToString() + ' harness checks passed; no installed game files, Steam processes or game code were used.')
}
finally {
    $resolved = [IO.Path]::GetFullPath($fixture)
    $allowed = [IO.Path]::GetFullPath($testParent).TrimEnd('\') + '\smoke-tests-'
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($testParent)) { throw 'Test cleanup target escaped its fixture directory.' }
    [IO.Directory]::Delete($resolved, $true)
}
