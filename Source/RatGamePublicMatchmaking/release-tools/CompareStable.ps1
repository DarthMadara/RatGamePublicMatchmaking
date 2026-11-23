$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$game = [IO.Path]::GetFullPath((Join-Path $project '..\..'))
$release = Join-Path $game 'MatchmakingDevelopment\ReleaseCandidates\0.2.0'
$oldPath = Join-Path $release 'preserved\RatGamePublicMatchmaking-v0.2.0-dev.1.dll'
$newPath = Join-Path $project 'src\bin\Release\netstandard2.1\RatGamePublicMatchmaking.dll'
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
$before = Get-Content -LiteralPath (Join-Path $release 'preserved\Before.json') -Raw | ConvertFrom-Json
. (Join-Path $PSScriptRoot 'VersionResource.ps1')
$oldVersionResource = Get-VersionResourceTree $oldPath '0.2.0-dev.1'
$newVersionResource = Get-VersionResourceTree $newPath '0.2.0'
if (($oldVersionResource | ConvertTo-Json -Depth 30 -Compress) -cne ($newVersionResource | ConvertTo-Json -Depth 30 -Compress)) {
    throw 'Windows version resources differ beyond ProductVersion text.'
}
$sourceFiles = @(Get-ChildItem -LiteralPath (Join-Path $project 'src') -File)
if ($sourceFiles.Count -ne $before.Source.Count) { throw 'Source file set changed.' }
foreach ($file in $sourceFiles) {
    if ($file.Name -in @('Plugin.cs','RatGamePublicMatchmaking.csproj')) {
        $oldText = [IO.File]::ReadAllText((Join-Path $release ('preserved\' + $file.Name)))
        $newText = [IO.File]::ReadAllText($file.FullName)
        if ($oldText.Replace('0.2.0-dev.1','0.2.0') -cne $newText) { throw ('Non-version source edit: ' + $file.Name) }
    } elseif ((Hash $file.FullName) -ne @($before.Source | Where-Object {$_.Name -eq $file.Name})[0].SHA256) {
        throw ('Implementation changed: ' + $file.Name)
    }
}
Add-Type -Path (Join-Path $game 'BepInEx\core\Mono.Cecil.dll')
$old = [Mono.Cecil.ModuleDefinition]::ReadModule($oldPath)
$stable = [Mono.Cecil.ModuleDefinition]::ReadModule($newPath)
try {
    $oldInfo = @($old.Assembly.CustomAttributes | Where-Object {$_.AttributeType.Name -eq 'AssemblyInformationalVersionAttribute'})[0]
    $stableInfo = @($stable.Assembly.CustomAttributes | Where-Object {$_.AttributeType.Name -eq 'AssemblyInformationalVersionAttribute'})[0]
    if ($oldInfo.ConstructorArguments[0].Value -ne '0.2.0-dev.1' -or $stableInfo.ConstructorArguments[0].Value -ne '0.2.0') { throw 'Unexpected informational versions.' }
    $oldInfo.ConstructorArguments[0] = New-Object Mono.Cecil.CustomAttributeArgument($old.TypeSystem.String,'0.2.0')
    $oldAwake = @($old.Types | Where-Object {$_.Name -eq 'Plugin'})[0].Methods | Where-Object {$_.Name -eq 'Awake'}
    $oldLog = @($oldAwake.Body.Instructions | Where-Object {$_.OpCode.Name -eq 'ldstr' -and $_.Operand -is [string] -and $_.Operand.Contains('0.2.0-dev.1 by Madara loaded.')})
    if ($oldLog.Count -ne 1) { throw 'Expected exactly one startup version string.' }
    $oldLog[0].Operand = $oldLog[0].Operand.Replace('0.2.0-dev.1','0.2.0')
    # Independently parsed/compared the complete Windows version-resource tree
    # above. Omit only that tree for re-emission because changing ProductVersion
    # changes its physical string length. Compare all remaining IL/metadata bytes.
    foreach ($module in @($old,$stable)) {
        $image = $module.GetType().GetField('Image',[Reflection.BindingFlags]'NonPublic,Instance').GetValue($module)
        $resourceField = $image.GetType().GetField('Win32Resources',[Reflection.BindingFlags]'Public,NonPublic,Instance')
        $resourceField.SetValue($image,[Activator]::CreateInstance($resourceField.FieldType))
    }
    $stable.Mvid = $old.Mvid
    $parameters = New-Object Mono.Cecil.WriterParameters
    $parameters.Timestamp = [uint32]0
    $parameters.WriteSymbols = $false
    $oldNormalized = Join-Path $release 'preserved\tested-normalized.dll'
    $stableNormalized = Join-Path $release 'preserved\stable-normalized.dll'
    $old.Write($oldNormalized,$parameters)
    $stable.Write($stableNormalized,$parameters)
    if ((Hash $oldNormalized) -ne (Hash $stableNormalized)) { throw 'Normalized complete assemblies differ; stop before publication and investigate.' }
    if ($stable.Assembly.Name.Version.ToString() -ne '0.2.0.0' -or $stable.Resources.Count -ne 0) { throw 'Unexpected assembly version or embedded resources.' }
    $plugin = @($stable.Types | Where-Object {$_.Name -eq 'Plugin'})[0]
    $bepVersion = @($plugin.CustomAttributes | Where-Object {$_.AttributeType.FullName -eq 'BepInEx.BepInPlugin'})[0].ConstructorArguments[2].Value
    if ($bepVersion -ne '0.2.0') { throw 'Unexpected plugin version.' }
    $report = [pscustomobject]@{CheckedUtc=[DateTime]::UtcNow.ToString('O');TestedSHA256=(Hash $oldPath);StableSHA256=(Hash $newPath);
        StableInformationalVersion='0.2.0';AssemblyFileVersion='0.2.0.0';BepInExPluginVersion=$bepVersion;
        SourceOnlyVersionEdits=$true;CompleteNormalizedAssemblyBytesEqual=$true;WindowsResourceTreeOnlyProductVersionTextChanged=$true;NormalizedSHA256=(Hash $oldNormalized);
        AllowedDifferences=@('AssemblyInformationalVersion and Windows ProductVersion: 0.2.0-dev.1 to 0.2.0','Plugin.Awake startup version text: 0.2.0-dev.1 to 0.2.0','Compiler MVID/PE timestamp/build symbols');
        MeaningfulImplementationChanges=$false;EmbeddedResources=0;LiveStableBinarySessionPerformed=$false}
    $report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $release 'BinaryComparison.json')
    $report | ConvertTo-Json -Depth 5
} finally { $old.Dispose(); $stable.Dispose() }
