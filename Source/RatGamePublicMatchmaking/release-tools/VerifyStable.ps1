$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
Add-Type -AssemblyName System.IO.Compression.FileSystem
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$game = [IO.Path]::GetFullPath((Join-Path $project '..\..'))
$release = Join-Path $game 'MatchmakingDevelopment\ReleaseCandidates\0.2.0'
$zipPath = Join-Path $release 'RatGamePublicMatchmaking-v0.2.0.zip'
$extract = Join-Path $release ('extracted-verification-' + [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfffZ'))
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
$script:checks = 0
function Check([bool]$Condition,[string]$Label) {
    if (!$Condition) { throw ('FAIL: ' + $Label) }
    $script:checks++; Write-Output ('PASS: ' + $Label)
}
$report = Get-Content -LiteralPath (Join-Path $release 'PACKAGE-VERIFICATION.json') -Raw | ConvertFrom-Json
$before = Get-Content -LiteralPath (Join-Path $release 'preserved\Before.json') -Raw | ConvertFrom-Json
$allowed = @('BepInEx/plugins/RatGamePublicMatchmaking/RatGamePublicMatchmaking.dll','CHANGELOG.txt',
    'INSTALL.txt','KNOWN LIMITATIONS.txt','LICENSE.txt','README.txt','RELEASE-MANIFEST.json','SHA256SUMS.txt',
    'START HERE.txt','THIRD_PARTY_NOTICES.txt','UNINSTALL.txt','Validation/TEST-SUMMARY.txt','ZIP-CONTENTS.txt')
Check ((Hash $zipPath) -eq $report.ZipSHA256) 'Whole ZIP matches recorded checksum'
Check (!(Test-Path -LiteralPath $extract)) 'Independent extraction uses a new empty destination'
$zip = [IO.Compression.ZipFile]::OpenRead($zipPath)
try {
    $names = @($zip.Entries | ForEach-Object {$_.FullName})
    Check ($names.Count -eq 13 -and @($names | Select-Object -Unique).Count -eq 13) 'Exactly 13 distinct entries'
    Check (@(Compare-Object $names $allowed).Count -eq 0) 'Archive matches independent allowlist'
    foreach ($name in $names) {
        Check (!$name.Contains('..') -and !$name.Contains(':') -and !$name.Contains('\') -and !$name.StartsWith('/')) ('Safe archive path: ' + $name)
    }
} finally { $zip.Dispose() }
[IO.Compression.ZipFile]::ExtractToDirectory($zipPath,$extract)
$sums = @(Get-Content -LiteralPath (Join-Path $extract 'SHA256SUMS.txt'))
Check ($sums.Count -eq 12) 'Manifest covers every other archive file'
$covered = @()
foreach ($line in $sums) {
    if ($line -notmatch '^([A-F0-9]{64})  (.+)$') { throw 'Malformed checksum entry.' }
    $expected = $Matches[1]; $relative = $Matches[2]; $covered += $relative
    Check ((Hash (Join-Path $extract $relative)) -eq $expected) ('Extracted checksum: ' + $relative)
}
Check (@(Compare-Object @($names | Where-Object {$_ -ne 'SHA256SUMS.txt'}) $covered).Count -eq 0) 'Checksum paths exactly cover payload'
Check (@(Compare-Object $names @(Get-Content -LiteralPath (Join-Path $extract 'ZIP-CONTENTS.txt'))).Count -eq 0) 'Inventory lists every file'
Check (@($names | Where-Object {$_ -match '(?i)(/config/|bans\.v1|LogOutput|\.pdb$|\.png$|\.ttf$|Assembly-CSharp|UnityEngine|Steamworks|Rat Game_Data)'}).Count -eq 0) 'No config, banlist, log, symbols, assets or proprietary assemblies'
$dll = Join-Path $extract 'BepInEx\plugins\RatGamePublicMatchmaking\RatGamePublicMatchmaking.dll'
Check ((Hash $dll) -eq $report.DllSHA256) 'Extracted DLL checksum matches stable Release build'
Add-Type -Path (Join-Path $game 'BepInEx\core\Mono.Cecil.dll')
$module = [Mono.Cecil.ModuleDefinition]::ReadModule($dll)
try {
    $version = @($module.Assembly.CustomAttributes | Where-Object {$_.AttributeType.Name -eq 'AssemblyInformationalVersionAttribute'})[0].ConstructorArguments[0].Value
    $plugin = $module.Types | Where-Object {$_.Name -eq 'Plugin'}
    $pluginVersion = @($plugin.CustomAttributes | Where-Object {$_.AttributeType.FullName -eq 'BepInEx.BepInPlugin'})[0].ConstructorArguments[2].Value
    $fileVersion = [Diagnostics.FileVersionInfo]::GetVersionInfo($dll)
    Check ($version -eq '0.2.0' -and $pluginVersion -eq '0.2.0' -and $fileVersion.ProductVersion -eq '0.2.0' -and
        $module.Assembly.Name.Version.ToString() -eq '0.2.0.0' -and $fileVersion.FileVersion -eq '0.2.0.0') 'All stable versions verified from extracted DLL'
    Check ($module.Resources.Count -eq 0) 'No embedded managed resources'
    foreach ($reference in $module.AssemblyReferences) {
        Check ((Test-Path -LiteralPath (Join-Path $game ('BepInEx\core\' + $reference.Name + '.dll'))) -or
            (Test-Path -LiteralPath (Join-Path $game ('Rat Game_Data\Managed\' + $reference.Name + '.dll')))) ('Dependency exists in supported loader/game: ' + $reference.Name)
    }
    function AllTypes($Types) { foreach ($type in $Types) { $type; AllTypes $type.NestedTypes } }
    $logFound = $false
    foreach ($type in AllTypes $module.Types) {
        foreach ($method in @($type.Methods | Where-Object {$_.HasBody})) {
            foreach ($instruction in $method.Body.Instructions) {
                if ($instruction.Operand -isnot [string]) { continue }
                $value = $instruction.Operand
                if ($value.Contains('0.2.0 by Madara loaded.')) { $logFound = $true }
                if ($value -match '(?i)C:\\Users\\|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|Bearer [A-Za-z0-9]|7656119[0-9]{10}') { throw 'Unexpected private constant in DLL.' }
                if ($value.Contains('0.2.0-dev.1')) { throw 'Development-version IL string remains.' }
            }
        }
    }
    Check $logFound 'Startup log identifies stable version'
} finally { $module.Dispose() }
$testSummary = [IO.File]::ReadAllText((Join-Path $extract 'Validation\TEST-SUMMARY.txt'))
Check ($testSummary.Contains($report.DllSHA256) -and $testSummary.Contains('participant reports')) 'Test summary identifies exact stable DLL and report provenance'
$install = [IO.File]::ReadAllText((Join-Path $extract 'INSTALL.txt'))
Check ($install.Contains('Preserve BepInEx/config/local.ratgame.publicmatchmaking.cfg') -and $install.Contains('No configuration or banlist is in this ZIP.')) 'Instructions preserve user config and bans'
$readme = [IO.File]::ReadAllText((Join-Path $extract 'README.txt'))
Check ($readme.Contains('AI Usage Disclosure') -and $readme.Contains('No AI-generated artwork') -and $readme.Contains('Sokpop Collective')) 'AI disclosure and Sokpop credit retained'
foreach ($file in $before.ProtectedFiles) {
    if (!(Test-Path -LiteralPath $file.Path) -or (Hash $file.Path) -ne $file.SHA256) { throw ('Protected file changed: ' + $file.Path) }
}
Check $true ('All ' + $before.ProtectedFiles.Count + ' installed/beta/Eric files unchanged')
$oldBeta = Join-Path $game 'MatchmakingDevelopment\ReleaseCandidates\0.1.0-beta.1\package\GameFiles\BepInEx\plugins\RatGamePublicMatchmaking\RatGamePublicMatchmaking.dll'
Check ((Hash $oldBeta) -eq '37837EB373022F256518BE789A9FCBCE47D66CC3656E8B1D261478C138C78684') 'Original beta DLL is preserved'
$oldZipSum = Join-Path $game 'MatchmakingDevelopment\ReleaseCandidates\0.1.0-beta.1\artifacts\RatGamePublicMatchmaking-0.1.0-beta.1-Windows-x64.zip.sha256'
Check ([IO.File]::ReadAllText($oldZipSum).Contains('EA96BC7385FF62CAD1D27130F4CDDBB9D7EAAA23F0F970D43546792CD88E13A1')) 'Original beta archive checksum record is preserved (local ZIP is absent)'
$repository = Join-Path $release 'repository'
# Add this verification tool after the initial staging operation. Only this known
# source file is copied; runtime payload and ZIP remain immutable.
[IO.File]::Copy($PSCommandPath,(Join-Path $repository 'Source\RatGamePublicMatchmaking\release-tools\VerifyStable.ps1'),$false)
$repoFiles = @(Get-ChildItem -LiteralPath $repository -File -Recurse)
Check (@($repoFiles | Where-Object {$_.Extension -in @('.dll','.zip','.pdb','.png','.xml','.cfg','.log')}).Count -eq 0) 'Repository staging contains source/docs only'
$repoNames = @($repoFiles | ForEach-Object {$_.FullName.Substring($repository.Length + 1).Replace('\','/')} | Sort-Object)
[IO.File]::WriteAllLines((Join-Path $release 'REPOSITORY-FILES.txt'),$repoNames,(New-Object Text.UTF8Encoding($false)))
$result = [pscustomobject]@{CheckedUtc=[DateTime]::UtcNow.ToString('O');OfflinePackageChecks=$script:checks;
    ZipSHA256=(Hash $zipPath);DllSHA256=(Hash $dll);ExtractedFiles=$names.Count;IndependentExtractionAndHashes=$true;
    ProtectedFilesUnchanged=$before.ProtectedFiles.Count;BetaReleasePreserved=$true;RepositorySourceAndDocFiles=$repoFiles.Count;
    NewGameSessionPerformed=$false;LiveStableStampedBinaryVerified=$false;GitHubPublished=$false;NexusPublished=$false}
$result | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $release 'INDEPENDENT-VERIFICATION.json')
$result | ConvertTo-Json
