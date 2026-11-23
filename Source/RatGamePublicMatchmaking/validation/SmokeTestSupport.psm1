Set-StrictMode -Version 2

function Get-SmokeHash([string]$Path) {
    # BepInEx holds an active writer. A reader must share Write as well as Read;
    # Get-FileHash's default sharing can reject an otherwise readable live log.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
        ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
    $sha = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($sha.ComputeHash($stream)).Replace('-', '') }
    finally { $sha.Dispose(); $stream.Dispose() }
}

function Get-SmokeText([string]$Path) {
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { return '' }
    $stream = $null; $reader = $null
    try {
        $stream = [IO.File]::Open($Path, [IO.FileMode]::Open, [IO.FileAccess]::Read,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        $reader = New-Object IO.StreamReader($stream)
        return [string]$reader.ReadToEnd()
    }
    catch [IO.IOException] { return '' } # A truncated/rotating log is a poll miss.
    finally {
        if ($null -ne $reader) { $reader.Dispose() }
        elseif ($null -ne $stream) { $stream.Dispose() }
    }
}

function Get-SmokeObservation([string]$LogText) {
    $text = [string]$LogText
    $starts = [regex]::Matches($text, '(?m)^\[Message:\s*BepInEx\] BepInEx 5\.')
    if ($starts.Count -gt 0) { $text = $text.Substring($starts[$starts.Count - 1].Index) }
    $ready = [regex]::Match($text, 'clientReady=True; appsInterfaceReady=True; build=([1-9][0-9]*); reason=ready')
    return [pscustomobject]@{
        PluginInitialized = $text.Contains('0.2.0-dev.1 by Madara loaded.')
        ChainloaderComplete = $text.Contains('Chainloader startup complete')
        SteamReady = $ready.Success
        SteamBuild = $(if ($ready.Success) { $ready.Groups[1].Value } else { $null })
        HostLobbyCreated = $text.Contains('Host lobby created.')
        WaitingLobby = [regex]::IsMatch($text, 'Host lifecycle=waiting; Public Hosting requested=(True|False)')
        NativeUiInitialized = $text.Contains('Main menu public browser installed')
        SteamInitFailed = $text.Contains('SteamApi_Init returned false')
    }
}

function Add-SmokeError {
    param($Session, [ValidateSet('Primary','Cleanup')][string]$Category, [string]$Stage, $Failure)
    $message = [string]$Failure
    $errorType = ''
    $stack = ''
    if ($Failure -is [Management.Automation.ErrorRecord]) {
        $message = $Failure.Exception.Message
        $errorType = $Failure.Exception.GetType().FullName
        $stack = [string]$Failure.ScriptStackTrace
    }
    $record = [pscustomobject]@{ AtUtc=[DateTime]::UtcNow.ToString('O'); Stage=$Stage;
        Message=$message; ExceptionType=$errorType; ScriptStackTrace=$stack }
    if ($Category -eq 'Primary') { $Session.PrimaryErrors = @($Session.PrimaryErrors) + @($record) }
    else { $Session.CleanupErrors = @($Session.CleanupErrors) + @($record) }
    return $record
}

function Set-SmokeFile {
    param([string]$Source, [string]$Destination, [scriptblock]$EnsureStopped)
    # This callback MUST check live processes on every attempt, including retries.
    if ($null -eq $EnsureStopped) { throw 'A game-process guard is required.' }
    & $EnsureStopped
    $sourceHash = Get-SmokeHash $Source
    $temporary = $Destination + '.rgpm-smoke-' + [Guid]::NewGuid().ToString('N') + '.tmp'
    $operationFailure = $null; $temporaryCleanupFailure = $null
    try {
        [IO.File]::Copy($Source, $temporary, $false)
        $file = [IO.File]::Open($temporary, [IO.FileMode]::Open, [IO.FileAccess]::Write, [IO.FileShare]::None)
        try { $file.Flush($true) } finally { $file.Dispose() }
        if ((Get-SmokeHash $temporary) -ne $sourceHash) { throw 'Temporary copy hash mismatch.' }
        & $EnsureStopped
        if ([IO.File]::Exists($Destination)) { [IO.File]::Replace($temporary, $Destination, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $Destination) }
        if ((Get-SmokeHash $Destination) -ne $sourceHash) { throw 'Installed/restored file hash mismatch.' }
    }
    catch { $operationFailure = $_ }
    finally {
        if ([IO.File]::Exists($temporary)) {
            try { [IO.File]::Delete($temporary) } catch { $temporaryCleanupFailure = $_ }
        }
    }
    if ($null -ne $operationFailure) {
        if ($null -ne $temporaryCleanupFailure) { $operationFailure.Exception.Data['TemporaryCleanupFailure'] = $temporaryCleanupFailure.Exception.Message }
        throw $operationFailure
    }
    if ($null -ne $temporaryCleanupFailure) { throw $temporaryCleanupFailure }
}

Export-ModuleMember -Function Get-SmokeHash,Get-SmokeText,Get-SmokeObservation,Add-SmokeError,Set-SmokeFile
