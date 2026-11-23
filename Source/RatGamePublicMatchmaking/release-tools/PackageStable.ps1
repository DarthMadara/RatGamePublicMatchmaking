$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
Add-Type -AssemblyName System.IO.Compression.FileSystem
Add-Type -AssemblyName System.IO.Compression
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$game = [IO.Path]::GetFullPath((Join-Path $project '..\..'))
$release = Join-Path $game 'MatchmakingDevelopment\ReleaseCandidates\0.2.0'
$package = Join-Path $release 'package'
$repository = Join-Path $release 'repository'
$zipPath = Join-Path $release 'RatGamePublicMatchmaking-v0.2.0.zip'
$dllEntry = 'BepInEx/plugins/RatGamePublicMatchmaking/RatGamePublicMatchmaking.dll'
$candidate = Join-Path $project 'src\bin\Release\netstandard2.1\RatGamePublicMatchmaking.dll'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
function WriteText([string]$Path,[string]$Body) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Path)) -Force | Out-Null
    [IO.File]::WriteAllText($Path,$Body.Replace("`r`n","`n").Replace("`n","`r`n"),(New-Object Text.UTF8Encoding($false)))
}
function CopyFile([string]$Source,[string]$Target) {
    New-Item -ItemType Directory -Path ([IO.Path]::GetDirectoryName($Target)) -Force | Out-Null
    [IO.File]::Copy($Source,$Target,$false)
}
$comparison = Get-Content -LiteralPath (Join-Path $release 'BinaryComparison.json') -Raw | ConvertFrom-Json
$validation = Get-Content -LiteralPath (Join-Path $project 'artifacts\Validation.json') -Raw | ConvertFrom-Json
$before = Get-Content -LiteralPath (Join-Path $release 'preserved\Before.json') -Raw | ConvertFrom-Json
$dllHash = Hash $candidate
if ($dllHash -ne $comparison.StableSHA256 -or $dllHash -ne $validation.PluginSHA256 -or
    $validation.PluginInformationalVersion -ne '0.2.0' -or !$comparison.CompleteNormalizedAssemblyBytesEqual -or
    !$comparison.WindowsResourceTreeOnlyProductVersionTextChanged -or $comparison.MeaningfulImplementationChanges) { throw 'Stable binary comparison or validation is incomplete.' }
$buildLog = [IO.File]::ReadAllText((Join-Path $release 'BuildAndTests.log'))
foreach ($required in @('176 offline safety, presentation and moderation checks passed.',
    'Validated 30 exact Harmony targets and 1804 external member references','0 Warning(s)','0 Error(s)')) {
    if (!$buildLog.Contains($required)) { throw ('Missing stable validation evidence: ' + $required) }
}
if (![IO.File]::ReadAllText((Join-Path $release 'HarnessTests.log')).Contains('25 harness checks passed')) { throw 'Harness checks incomplete.' }
if ((Test-Path -LiteralPath $package) -or (Test-Path -LiteralPath $repository) -or (Test-Path -LiteralPath $zipPath)) { throw 'Package/staging already exists; refuse to overwrite.' }
CopyFile $candidate (Join-Path $package $dllEntry)
$rootDocs = @('INSTALL.txt','UNINSTALL.txt','CHANGELOG.txt','LICENSE.txt','THIRD_PARTY_NOTICES.txt','KNOWN LIMITATIONS.txt')
foreach ($name in $rootDocs) { CopyFile (Join-Path $project $name) (Join-Path $package $name) }
foreach ($name in @('README.txt','START HERE.txt')) { CopyFile (Join-Path $project ('release-docs\' + $name)) (Join-Path $package $name) }
CopyFile (Join-Path $project 'release-docs\TEST-SUMMARY.txt') (Join-Path $package 'Validation\TEST-SUMMARY.txt')
$manifest = [ordered]@{Name='Rat Game Public Matchmaking';Author='Madara';Version='0.2.0';Configuration='Release';
    PackageType='Plugin update; supported BepInEx prerequisite required';PluginSHA256=$dllHash;
    PluginInformationalVersion='0.2.0';PluginFileVersion='0.2.0.0';BepInExPluginVersion='0.2.0';
    SteamAppId=3347910;ValidatedSteamBuild=26403247;RequiredBepInEx='5.4.23.5 Windows x64';
    TestedDevelopmentSHA256=$comparison.TestedSHA256;OnlyVersionMetadataAndStartupTextChanged=$true;
    OfflineModChecks=176;OfflineHarnessChecks=25;ExactHarmonyTargets=30;ExternalReferences=1804;
    ParticipantConfirmedTwoAccountActions=@('Kick','Ban','Unban');ParticipantConfirmedMilestoneDate='2026-11-22';
    OrganicPublicSession='Unrelated player discovered public lobby, joined and played two games; participant-confirmed';
    LiveStableStampedDllSessionPerformed=$false;AllMultiplayerScenariosVerified=$false;
    ConfigurationOrBanlistsSupplied=$false;LoaderDependenciesBundled=$false;OriginalAssetsOrAssembliesBundled=$false;
    NewArtisticAssetsBundled=$false;PublicHostingDefaultsOff=$true;NativeUiToolsDefaultsOff=$true;
    ChecksumCoverage='SHA256SUMS.txt covers every other ZIP entry; external .sha256 covers ZIP'}
WriteText (Join-Path $package 'RELEASE-MANIFEST.json') ($manifest | ConvertTo-Json -Depth 5)
$allowed = @(@($dllEntry) + $rootDocs + @('README.txt','START HERE.txt','Validation/TEST-SUMMARY.txt','RELEASE-MANIFEST.json','ZIP-CONTENTS.txt','SHA256SUMS.txt') | Sort-Object)
WriteText (Join-Path $package 'ZIP-CONTENTS.txt') (($allowed -join "`r`n") + "`r`n")
$sumLines = @($allowed | Where-Object {$_ -ne 'SHA256SUMS.txt'} | ForEach-Object {(Hash (Join-Path $package $_)) + '  ' + $_})
WriteText (Join-Path $package 'SHA256SUMS.txt') (($sumLines -join "`r`n") + "`r`n")
$files = @(Get-ChildItem -LiteralPath $package -File -Recurse)
if ($files.Count -ne 13 -or $allowed.Count -ne 13) { throw 'Unexpected package file count.' }
foreach ($file in $files) {
    $relative = $file.FullName.Substring($package.Length + 1).Replace('\','/')
    if ($relative -cnotin $allowed) { throw ('Unexpected payload: ' + $relative) }
    if ($file.Extension -in @('.txt','.json','.md') -and [IO.File]::ReadAllText($file.FullName) -match '(?i)C:\\Users\\|BEGIN (RSA |EC |OPENSSH )?PRIVATE KEY|Bearer [A-Za-z0-9]|7656119[0-9]{10}') { throw 'Unexpected private material in package text.' }
}
$zip = [IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
try { foreach ($entry in $allowed) { $null = [IO.Compression.ZipFileExtensions]::CreateEntryFromFile($zip,(Join-Path $package $entry),$entry,[IO.Compression.CompressionLevel]::Optimal) } }
finally { $zip.Dispose() }
$zipHash = Hash $zipPath
WriteText ($zipPath + '.sha256') ($zipHash + '  ' + [IO.Path]::GetFileName($zipPath) + "`r`n")

# Stage source and public docs separately; never include private evidence/backups,
# reference assemblies, logs, binaries, screenshots or local ban/config files.
foreach ($name in @($rootDocs + @('README.txt','START HERE.txt','RELEASE-MANIFEST.json','ZIP-CONTENTS.txt','SHA256SUMS.txt','Validation/TEST-SUMMARY.txt'))) {
    CopyFile (Join-Path $package $name) (Join-Path $repository $name)
}
CopyFile (Join-Path $project 'README.md') (Join-Path $repository 'README.md')
CopyFile (Join-Path $project 'release-docs\RELEASE-NOTES.md') (Join-Path $release 'RELEASE-NOTES.md')
$sourceRoot = Join-Path $repository 'Source\RatGamePublicMatchmaking'
foreach ($name in @('BUILDING.txt','LICENSE.txt','NuGet.Config','build.cmd','.gitignore','AGENTS.md','CHANGES.txt')) {
    CopyFile (Join-Path $project $name) (Join-Path $sourceRoot $name)
}
foreach ($directory in @('src','tests')) {
    foreach ($file in Get-ChildItem -LiteralPath (Join-Path $project $directory) -File) {
        if ($file.Extension -notin @('.cs','.csproj')) { throw 'Unexpected source file type.' }
        CopyFile $file.FullName (Join-Path $sourceRoot ($directory + '\' + $file.Name))
    }
}
foreach ($name in @('Validation.csproj','Program.cs','ModerationVerification.cs','RuntimeVerification.cs','SmokeTest.ps1','SmokeTestSupport.psm1','SmokeTest.Tests.ps1')) {
    CopyFile (Join-Path $project ('validation\' + $name)) (Join-Path $sourceRoot ('validation\' + $name))
}
foreach ($file in Get-ChildItem -LiteralPath $PSScriptRoot -File) {
    if ($file.Extension -eq '.ps1') { CopyFile $file.FullName (Join-Path $sourceRoot ('release-tools\' + $file.Name)) }
}
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $project 'release-docs') -File) {
    CopyFile $file.FullName (Join-Path $sourceRoot ('release-docs\' + $file.Name))
}
$repositoryFiles = @(Get-ChildItem -LiteralPath $repository -File -Recurse | ForEach-Object {$_.FullName.Substring($repository.Length + 1).Replace('\','/')})
WriteText (Join-Path $release 'REPOSITORY-FILES.txt') (($repositoryFiles -join "`r`n") + "`r`n")
foreach ($file in $before.ProtectedFiles) { if (!(Test-Path -LiteralPath $file.Path) -or (Hash $file.Path) -ne $file.SHA256) { throw ('Protected installed/beta/test-package file changed: ' + $file.Path) } }
$result = [ordered]@{CheckedUtc=[DateTime]::UtcNow.ToString('O');ZipPath=$zipPath;ZipSHA256=$zipHash;
    ZipBytes=(Get-Item -LiteralPath $zipPath).Length;Entries=$allowed;DllSHA256=$dllHash;
    ProtectedFilesUnchanged=$before.ProtectedFiles.Count;BetaReleaseAndEricPackagePreserved=$true;
    InstalledFilesModified=$false;ConfigOrBanlistsBundled=$false;ProprietaryAssetsOrAssembliesBundled=$false;
    StableImplementationEquivalent=$true;GitHubReleasePublished=$false;NexusPublished=$false;RepositoryStagingFiles=$repositoryFiles.Count}
WriteText (Join-Path $release 'PACKAGE-VERIFICATION.json') ($result | ConvertTo-Json -Depth 5)
[pscustomobject]$result | Select-Object ZipPath,ZipSHA256,ZipBytes,DllSHA256,ProtectedFilesUnchanged,RepositoryStagingFiles,GitHubReleasePublished | ConvertTo-Json
