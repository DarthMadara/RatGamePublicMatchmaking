$ErrorActionPreference = 'Stop'
Set-StrictMode -Version 2
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$game = [IO.Path]::GetFullPath((Join-Path $project '..\..'))
$release = Join-Path $game 'MatchmakingDevelopment\ReleaseCandidates\0.2.0'
if (Test-Path -LiteralPath $release) { throw 'Stable preparation already exists; refuse to overwrite evidence.' }
New-Item -ItemType Directory -Path (Join-Path $release 'preserved') -Force | Out-Null
function Hash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash }
$candidate = Join-Path $project 'src\bin\Release\netstandard2.1\RatGamePublicMatchmaking.dll'
$tested = Join-Path $game 'MatchmakingDevelopment\PrivateTesting\Eric\v0.2.0-dev.1\full-install\package\GameFiles\BepInEx\plugins\RatGamePublicMatchmaking\RatGamePublicMatchmaking.dll'
$expected = 'CCA2EAE1C4071437D02A023108055CEB7C3D2DFB1EF1B191FDE2538A4C20E8BD'
if ((Hash $candidate) -ne $expected -or (Hash $tested) -ne $expected) { throw 'Candidate differs from the Eric-tested implementation.' }
[IO.File]::Copy($candidate,(Join-Path $release 'preserved\RatGamePublicMatchmaking-v0.2.0-dev.1.dll'),$false)
$source = @()
foreach ($file in Get-ChildItem -LiteralPath (Join-Path $project 'src') -File) {
    $source += [pscustomobject]@{Name=$file.Name;SHA256=(Hash $file.FullName)}
    [IO.File]::Copy($file.FullName,(Join-Path $release ('preserved\' + $file.Name)),$false)
}
$protected = @()
$paths = @((Join-Path $game 'BepInEx'),(Join-Path $game 'MatchmakingDevelopment\ReleaseCandidates\0.1.0-beta.1'),
    (Join-Path $game 'MatchmakingDevelopment\PrivateTesting\Eric\v0.2.0-dev.1'))
foreach ($directory in $paths) {
    foreach ($file in Get-ChildItem -LiteralPath $directory -File -Recurse) {
        $protected += [pscustomobject]@{Path=$file.FullName;SHA256=(Hash $file.FullName)}
    }
}
foreach ($file in Get-ChildItem -LiteralPath $game -File) {
    $protected += [pscustomobject]@{Path=$file.FullName;SHA256=(Hash $file.FullName)}
}
$validation = Get-Content -LiteralPath (Join-Path $project 'artifacts\Validation.json') -Raw | ConvertFrom-Json
foreach ($assembly in $validation.GameAssembliesUnchanged.PSObject.Properties) {
    $path = Join-Path $game ('Rat Game_Data\Managed\' + $assembly.Name)
    if ((Hash $path) -ne $assembly.Value) { throw ('Investigated game assembly changed: ' + $assembly.Name) }
    $protected += [pscustomobject]@{Path=$path;SHA256=(Hash $path)}
}
[pscustomobject]@{PreparedUtc=[DateTime]::UtcNow.ToString('O');TestedDllSHA256=$expected;Source=$source;ProtectedFiles=$protected} |
    ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $release 'preserved\Before.json')
Write-Output ('Preserved tested DLL/source and hashes of ' + $protected.Count + ' installed, beta and Eric-package files.')
