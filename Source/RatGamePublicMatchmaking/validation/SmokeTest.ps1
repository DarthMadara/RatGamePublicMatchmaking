param(
    [ValidateSet('Prepare','Launch','Observe','Status','Restore')][string]$Action = 'Status',
    [string]$Session = '',
    [ValidateRange(0,300)][int]$WaitSeconds = 0
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
Import-Module (Join-Path $PSScriptRoot 'SmokeTestSupport.psm1') -Force
$projectRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$gameRoot = [IO.Path]::GetFullPath((Join-Path $projectRoot '..\..'))
$artifacts = Join-Path $projectRoot 'artifacts'
$activePath = Join-Path $artifacts 'active-smoke-session.txt'
$pluginPath = Join-Path $gameRoot 'BepInEx\plugins\RatGamePublicMatchmaking\RatGamePublicMatchmaking.dll'
$configPath = Join-Path $gameRoot 'BepInEx\config\local.ratgame.publicmatchmaking.cfg'
$logPath = Join-Path $gameRoot 'BepInEx\LogOutput.log'
$gameExe = Join-Path $gameRoot 'Rat Game.exe'
$candidate = Join-Path $projectRoot 'src\bin\Release\netstandard2.1\RatGamePublicMatchmaking.dll'
$sessionRoot = $null; $record = $null; $stage = $Action

function Get-SmokeProcesses {
    return @(Get-Process -Name 'Rat Game' -ErrorAction SilentlyContinue)
}
function Assert-SmokeGameStopped {
    $running = @(Get-SmokeProcesses)
    if ($running.Count -gt 0) { throw ('Rat Game is running (PID ' + (($running | ForEach-Object { $_.Id }) -join ',') + '); no installation/restoration write is allowed.') }
}
function Save-SmokeSession {
    $record.UpdatedUtc = [DateTime]::UtcNow.ToString('O')
    $json = $record | ConvertTo-Json -Depth 12
    $target = Join-Path $sessionRoot 'Session.json'
    $temporary = $target + '.tmp'
    [IO.File]::WriteAllText($temporary, $json, (New-Object Text.UTF8Encoding($false)))
    if ([IO.File]::Exists($target)) { [IO.File]::Replace($temporary, $target, [NullString]::Value) }
    else { [IO.File]::Move($temporary, $target) }
}
function Add-SmokeEvent([string]$Name, $Details) {
    $event = [pscustomobject]@{ AtUtc=[DateTime]::UtcNow.ToString('O'); Event=$Name; Details=$Details }
    [IO.File]::AppendAllText((Join-Path $sessionRoot 'Events.jsonl'), (($event | ConvertTo-Json -Depth 6 -Compress) + [Environment]::NewLine))
    Write-Output ($event.AtUtc + ' ' + $Name + ' ' + ($Details | ConvertTo-Json -Depth 4 -Compress))
}
function Read-SmokeSession {
    if ([string]::IsNullOrWhiteSpace($Session)) {
        if (!(Test-Path -LiteralPath $activePath)) { throw 'No active smoke session. Use -Action Prepare first.' }
        $script:Session = [IO.File]::ReadAllText($activePath).Trim()
    }
    $resolved = [IO.Path]::GetFullPath($Session)
    $allowed = [IO.Path]::GetFullPath($artifacts).TrimEnd('\') + '\smoke-steam-'
    if (!$resolved.StartsWith($allowed, [StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetDirectoryName($resolved) -ne [IO.Path]::GetFullPath($artifacts)) {
        throw 'Session must be a smoke-steam-* directory immediately inside this project artifacts directory.'
    }
    $script:sessionRoot = $resolved
    $script:record = Get-Content -LiteralPath (Join-Path $resolved 'Session.json') -Raw | ConvertFrom-Json
    if ($record.Schema -ne 2 -or $record.GameRoot -ne $gameRoot) { throw 'Session schema or installation root mismatch.' }
}
function Record-SmokeFailure([string]$Category, $Failure) {
    $savedError = Add-SmokeError -Session $record -Category $Category -Stage $stage -Failure $Failure
    [IO.File]::AppendAllText((Join-Path $sessionRoot ($Category + 'Errors.jsonl')), (($savedError | ConvertTo-Json -Compress) + [Environment]::NewLine))
    Add-SmokeEvent ($Category + 'Failure') $savedError
}
function Collect-SmokeEvidence {
    $text = Get-SmokeText $logPath
    if ($text.Length -gt 0) { [IO.File]::WriteAllText((Join-Path $sessionRoot 'BepInEx.log'), $text) }
    $unity = Join-Path $sessionRoot 'Unity.Player.log'
    $record.UnityLogAvailable = Test-Path -LiteralPath $unity -PathType Leaf
    # A manual Steam Library launch may use Unity's normal log instead. Do not
    # search user profiles or rely on it to infer hosting; BepInEx records readiness.
}
function Observe-SmokeSession {
    $processes = @(Get-SmokeProcesses)
    $record.GameRunning = $processes.Count -gt 0
    foreach ($process in $processes) {
        try {
            $started = $process.StartTime.ToUniversalTime()
            if ($started -lt [DateTime]::Parse($record.CreatedUtc).ToUniversalTime()) { continue }
            if ($process.Path -ne $gameExe) { continue } # Poll again if module path is not available yet.
            if (@($record.Processes | Where-Object { $_.Id -eq $process.Id -and $_.StartUtc -eq $started.ToString('O') }).Count -eq 0) {
                $info = [pscustomobject]@{ Id=$process.Id; StartUtc=$started.ToString('O'); FirstObservedUtc=[DateTime]::UtcNow.ToString('O'); PathVerified=$true; ExitObservedUtc=$null }
                $record.Processes = @($record.Processes) + @($info)
                Add-SmokeEvent 'GameProcessObserved' $info
            }
        }
        catch { Add-SmokeEvent 'ProcessInspectionDeferred' @{ Id=$process.Id; Reason=$_.Exception.GetType().Name } }
    }
    foreach ($seen in @($record.Processes)) {
        if ($null -eq $seen.ExitObservedUtc -and @($processes | Where-Object { $_.Id -eq $seen.Id }).Count -eq 0) {
            $seen.ExitObservedUtc = [DateTime]::UtcNow.ToString('O')
            Add-SmokeEvent 'GameProcessExitObserved' @{Id=$seen.Id;ObservedUtc=$seen.ExitObservedUtc}
        }
    }
    $text = Get-SmokeText $logPath
    $fresh = $false
    if ($text.Length -gt 0 -and @($record.Processes).Count -gt 0) {
        $latest = @($record.Processes | Sort-Object StartUtc -Descending)[0]
        if ($record.CurrentRunPid -ne $latest.Id) {
            $record.CurrentRunPid = $latest.Id; $record.CurrentRunStartedUtc = $latest.StartUtc
            foreach ($property in @('PluginInitialized','ChainloaderComplete','SteamReady','HostLobbyCreated','WaitingLobby','NativeUiInitialized','SteamInitFailed','RuntimeHostingVerified')) { $record.$property = $false }
            $record.SteamBuild = $null
        }
        $logInfo = Get-Item -LiteralPath $logPath
        $fresh = $logInfo.LastWriteTimeUtc -ge [DateTime]::Parse($latest.StartUtc).ToUniversalTime() -and
            $logInfo.LastWriteTimeUtc -ge [DateTime]::Parse($record.CreatedUtc).ToUniversalTime() -and
            (Get-SmokeHash $logPath) -ne $record.LogBeforeSHA256
    }
    $record.FreshLogObserved = $fresh
    if ($fresh) {
        $observed = Get-SmokeObservation $text
        foreach ($property in @('PluginInitialized','ChainloaderComplete','SteamReady','HostLobbyCreated','WaitingLobby','NativeUiInitialized')) {
            if ($observed.$property -and !$record.$property) {
                Add-SmokeEvent $property @{ObservedUtc=[DateTime]::UtcNow.ToString('O');GamePid=$record.CurrentRunPid;Source='Fresh current-run BepInEx log';SteamBuild=$observed.SteamBuild}
            }
            $record.$property = $observed.$property
        }
        if ($observed.SteamReady) { $record.SteamBuild = $observed.SteamBuild }
    }
    $unityText = Get-SmokeText (Join-Path $sessionRoot 'Unity.Player.log')
    $unityIsFresh = $unityText.Length -gt 0 -and $null -ne $record.CurrentRunStartedUtc -and
        (Get-Item -LiteralPath (Join-Path $sessionRoot 'Unity.Player.log')).LastWriteTimeUtc -ge [DateTime]::Parse($record.CurrentRunStartedUtc).ToUniversalTime()
    if ($unityIsFresh -and $unityText.Contains('SteamApi_Init returned false') -and !$record.SteamInitFailed) {
        $record.SteamInitFailed = $true
        Record-SmokeFailure 'Primary' 'SteamApi_Init returned false in the current Steam-launched Unity log.'
    }
    $record.RuntimeHostingVerified = $record.FreshLogObserved -and $record.PluginInitialized -and $record.ChainloaderComplete -and
        $record.SteamReady -and $record.HostLobbyCreated -and $record.WaitingLobby -and !$record.SteamInitFailed
    if ($record.RuntimeHostingVerified) { $record.State = 'VerifiedAwaitingExit' }
    elseif ($record.GameRunning) { $record.State = 'Running' }
    elseif ($record.State -ne 'Restored') { $record.State = 'AwaitingSteamLaunch' }
    Collect-SmokeEvidence
    Save-SmokeSession
}
function Restore-SmokeSession {
    # Refuse even to attempt a DLL/config replacement while any game process lives.
    if (@(Get-SmokeProcesses).Count -gt 0) {
        $record.State = 'RestoreDeferredGameRunning'; $record.GameRunning = $true
        Add-SmokeEvent 'RestorationDeferred' 'Quit Rat Game normally; no installed files were written.'
        Save-SmokeSession
        return
    }
    Collect-SmokeEvidence
    foreach ($item in @(
        @{Name='Plugin';Backup='InstalledPlugin.before.dll';Destination=$pluginPath;Expected=$record.OriginalPluginSHA256},
        @{Name='Configuration';Backup='Config.before.cfg';Destination=$configPath;Expected=$record.OriginalConfigSHA256}
    )) {
        $script:stage = 'Restore' + $item.Name
        try {
            $backup = Join-Path $sessionRoot $item.Backup
            if ((Get-SmokeHash $backup) -ne $item.Expected) { throw 'Backup hash changed; restoration refused.' }
            $restored = $false
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                Assert-SmokeGameStopped
                try { Set-SmokeFile -Source $backup -Destination $item.Destination -EnsureStopped { Assert-SmokeGameStopped }; $restored = $true; break }
                catch [IO.IOException] { if ($attempt -eq 19) { throw }; Start-Sleep -Milliseconds 250 }
            }
            if (!$restored -or (Get-SmokeHash $item.Destination) -ne $item.Expected) { throw 'Restoration hash verification failed.' }
            if ($item.Name -eq 'Plugin') { $record.InstalledPluginRestored = $true }
            else { $record.ConfigRestored = $true }
            Add-SmokeEvent ($item.Name + 'Restored') @{SHA256=$item.Expected;GameRunning=$false}
        }
        catch { Record-SmokeFailure 'Cleanup' $_ }
        Save-SmokeSession
    }
    $record.GameRunning = @(Get-SmokeProcesses).Count -gt 0
    if ($record.InstalledPluginRestored -and $record.ConfigRestored -and !$record.GameRunning) {
        $record.State = 'Restored'; $record.RestoredUtc = [DateTime]::UtcNow.ToString('O')
        if ((Test-Path -LiteralPath $activePath) -and [IO.File]::ReadAllText($activePath).Trim() -eq $sessionRoot) { Remove-Item -LiteralPath $activePath }
    }
    else { $record.State = 'RestorationPending' }
    Save-SmokeSession
}

try {
    if ($Action -eq 'Prepare') {
        Assert-SmokeGameStopped
        if (Test-Path -LiteralPath $activePath) { throw 'An active session exists; inspect and restore it before preparing another.' }
        $manifest = Get-Content -LiteralPath (Join-Path $artifacts 'Validation.json') -Raw | ConvertFrom-Json
        if ($manifest.PluginInformationalVersion -ne '0.2.0-dev.1' -or (Get-SmokeHash $candidate) -ne $manifest.PluginSHA256) { throw 'Candidate is not the validated development DLL.' }
        foreach ($assembly in $manifest.GameAssembliesUnchanged.PSObject.Properties) {
            if ((Get-SmokeHash (Join-Path $gameRoot ('Rat Game_Data\Managed\' + $assembly.Name))) -ne $assembly.Value) { throw ('Investigated game assembly changed: ' + $assembly.Name) }
        }
        $sessionRoot = Join-Path $artifacts ('smoke-steam-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
        New-Item -ItemType Directory -Path $sessionRoot | Out-Null
        $originalPluginHash = Get-SmokeHash $pluginPath
        if ($originalPluginHash -ne '37837EB373022F256518BE789A9FCBCE47D66CC3656E8B1D261478C138C78684') { throw 'Installed DLL is not the preserved v0.1 beta; refusing an unexpected replacement.' }
        [IO.File]::Copy($pluginPath, (Join-Path $sessionRoot 'InstalledPlugin.before.dll'), $false)
        [IO.File]::Copy($configPath, (Join-Path $sessionRoot 'Config.before.cfg'), $false)
        $logBeforeHash = $null
        if (Test-Path -LiteralPath $logPath) {
            [IO.File]::Copy($logPath, (Join-Path $sessionRoot 'BepInEx.before.log'), $false)
            $logBeforeHash = Get-SmokeHash $logPath
        }
        $configHash = Get-SmokeHash $configPath
        if ((Get-SmokeHash (Join-Path $sessionRoot 'InstalledPlugin.before.dll')) -ne $originalPluginHash -or
            (Get-SmokeHash (Join-Path $sessionRoot 'Config.before.cfg')) -ne $configHash) { throw 'Backup verification failed.' }
        $record = [pscustomobject]@{
            Schema=2; CreatedUtc=[DateTime]::UtcNow.ToString('O'); UpdatedUtc=$null; GameRoot=$gameRoot; State='BackedUp';
            CandidateSHA256=$manifest.PluginSHA256; CandidateVersion=$manifest.PluginInformationalVersion;
            OriginalPluginSHA256=$originalPluginHash; OriginalConfigSHA256=$configHash; LogBeforeSHA256=$logBeforeHash;
            Processes=@(); CurrentRunPid=$null; CurrentRunStartedUtc=$null; SteamLaunchRequests=@(); PrimaryErrors=@(); CleanupErrors=@();
            PluginInitialized=$false; ChainloaderComplete=$false; SteamReady=$false; SteamBuild=$null; SteamInitFailed=$false;
            HostLobbyCreated=$false; WaitingLobby=$false; NativeUiInitialized=$false; FreshLogObserved=$false;
            RuntimeHostingVerified=$false; TwoAccountMultiplayerTested=$false; ModerationActionsTested=$false;
            GameRunning=$false; UnityLogAvailable=$false; InstalledPluginRestored=$false; ConfigRestored=$false; RestoredUtc=$null
        }
        Save-SmokeSession
        [IO.File]::WriteAllText($activePath, $sessionRoot)
        Add-SmokeEvent 'BackupsVerified' @{PluginSHA256=$originalPluginHash;ConfigSHA256=$configHash;CandidateSHA256=$record.CandidateSHA256}
        $temporaryConfig = [IO.File]::ReadAllText((Join-Path $sessionRoot 'Config.before.cfg'))
        foreach ($key in @('PublicHosting','NativeUiTools')) {
            $pattern = '(?m)^' + $key + '\s*=\s*(true|false)[ \t]*\r?$'
            if ([regex]::Matches($temporaryConfig, $pattern).Count -ne 1) { throw ('Configuration key missing or ambiguous: ' + $key) }
            $temporaryConfig = [regex]::Replace($temporaryConfig, $pattern, ($key + ' = false'))
        }
        $preparedConfig = Join-Path $sessionRoot 'Config.development.cfg'
        [IO.File]::WriteAllText($preparedConfig, $temporaryConfig, (New-Object Text.UTF8Encoding($false)))
        $stage = 'InstallDevelopmentPlugin'
        Set-SmokeFile -Source $candidate -Destination $pluginPath -EnsureStopped { Assert-SmokeGameStopped }
        $stage = 'InstallTemporaryConfiguration'
        Set-SmokeFile -Source $preparedConfig -Destination $configPath -EnsureStopped { Assert-SmokeGameStopped }
        $record.State = 'Prepared'; Save-SmokeSession
        Add-SmokeEvent 'DevelopmentInstalled' @{SHA256=(Get-SmokeHash $pluginPath);PublicHosting=$false;NativeUiTools=$false;GameRunning=$false}
    }
    else {
        Read-SmokeSession
        if ($Action -eq 'Launch') {
            if ($record.State -eq 'Restored') { throw 'Session is restored; prepare a new session before launching development.' }
            Assert-SmokeGameStopped
            if ((Get-SmokeHash $pluginPath) -ne $record.CandidateSHA256) { throw 'Installed candidate hash differs from session.' }
            $steamExe = [IO.Path]::GetFullPath((Join-Path $gameRoot '..\..\..\steam.exe'))
            $unityLog = Join-Path $sessionRoot 'Unity.Player.log'
            $requested = [DateTime]::UtcNow.ToString('O')
            # The only launch target is Steam. No direct game launch or AppID override.
            $launcher = Start-Process -FilePath $steamExe -ArgumentList @('-applaunch','3347910','-logFile', ('"' + $unityLog + '"')) -WorkingDirectory $gameRoot -WindowStyle Hidden -PassThru
            $request = [pscustomobject]@{RequestedUtc=$requested;LauncherPid=$launcher.Id;Launcher='Steam -applaunch 3347910';Automatic=$true}
            $record.SteamLaunchRequests = @($record.SteamLaunchRequests) + @($request)
            Add-SmokeEvent 'SteamLaunchRequested' $request
            $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
            do {
                Observe-SmokeSession
                if ($record.GameRunning -or [DateTime]::UtcNow -ge $deadline) { break }
                Start-Sleep -Milliseconds 500
            } while ($true)
            if (!$record.GameRunning) {
                $record.State = 'AwaitingManualSteamLaunch'
                Add-SmokeEvent 'ManualSteamLaunchNeeded' 'Automatic Steam request produced no observed game process. Use Steam Library Play and normal Host; installation remains staged.'
            }
            Save-SmokeSession
        }
        elseif ($Action -eq 'Observe' -or $Action -eq 'Status') {
            if ($record.State -ne 'Restored') {
                $deadline = [DateTime]::UtcNow.AddSeconds($WaitSeconds)
                do {
                    Observe-SmokeSession
                    if ($record.RuntimeHostingVerified -or $Action -eq 'Status' -or [DateTime]::UtcNow -ge $deadline) { break }
                    Start-Sleep -Milliseconds 500
                } while ($true)
            }
        }
        elseif ($Action -eq 'Restore') { Observe-SmokeSession; Restore-SmokeSession }
    }
    $record | ConvertTo-Json -Depth 10
}
catch {
    $primary = $_
    if ($null -ne $record -and $null -ne $sessionRoot) {
        try { Record-SmokeFailure $(if ($Action -eq 'Restore') { 'Cleanup' } else { 'Primary' }) $primary; Save-SmokeSession }
        catch { Write-Warning ('Failed to persist error evidence: ' + $_.Exception.Message) }
        if ($Action -eq 'Prepare') {
            try { Restore-SmokeSession }
            catch { try { Record-SmokeFailure 'Cleanup' $_; Save-SmokeSession } catch { Write-Warning 'Additional cleanup recording failure.' } }
        }
    }
    Write-Error -ErrorRecord $primary -ErrorAction Continue
    exit 1
}
