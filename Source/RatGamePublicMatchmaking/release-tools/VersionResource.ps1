# Read the sole compiler-generated Windows RT_VERSION resource without executing
# the DLL. Compare every fixed field/string, excluding only ProductVersion text.
function Get-VersionResourceTree([string]$Path, [string]$ExpectedProductVersion) {
    $bytes = [IO.File]::ReadAllBytes($Path)
    function U16([int]$At) { [BitConverter]::ToUInt16($bytes,$At) }
    function U32([int]$At) { [BitConverter]::ToUInt32($bytes,$At) }
    function Align4([int]$At) { ($At + 3) -band -4 }
    $pe = [BitConverter]::ToInt32($bytes,60)
    $sectionCount = U16 ($pe + 6)
    $optional = $pe + 24
    $sectionTable = $optional + (U16 ($pe + 20))
    $directories = if ((U16 $optional) -eq 0x20b) { $optional + 112 } else { $optional + 96 }
    $resourceRva = U32 ($directories + 16)
    function RvaOffset([uint32]$Rva) {
        for ($i=0; $i -lt $sectionCount; $i++) {
            $section = $sectionTable + $i * 40
            $virtual = U32 ($section + 12)
            $size = [Math]::Max((U32 ($section + 8)),(U32 ($section + 16)))
            if ($Rva -ge $virtual -and $Rva -lt ($virtual + $size)) { return [int]((U32 ($section + 20)) + $Rva - $virtual) }
        }
        throw 'Resource RVA outside PE sections.'
    }
    $rootOffset = RvaOffset $resourceRva
    $directoryAt = $rootOffset
    $ids = @()
    for ($level=0; $level -lt 3; $level++) {
        if (((U16 ($directoryAt + 12)) + (U16 ($directoryAt + 14))) -ne 1) { throw 'Expected exactly one RT_VERSION resource, with one name/language.' }
        $id = U32 ($directoryAt + 16); $next = U32 ($directoryAt + 20)
        if (($id -band 0x80000000L) -ne 0) { throw 'Unexpected named version resource.' }
        $ids += $id
        if ($level -lt 2 -and ($next -band 0x80000000L) -eq 0) { throw 'Unexpected resource tree structure.' }
        if ($level -eq 2 -and ($next -band 0x80000000L) -ne 0) { throw 'Unexpected nested resource directory.' }
        $directoryAt = $rootOffset + ($next -band 0x7fffffff)
    }
    if ($ids[0] -ne 16) { throw 'Unexpected non-version Windows resource.' }
    $valueAt = RvaOffset (U32 $directoryAt)
    $valueSize = U32 ($directoryAt + 4)
    $codePage = U32 ($directoryAt + 8)
    $reserved = U32 ($directoryAt + 12)
    $script:productVersionFields = 0
    function Read-VersionNode([int]$At, [int]$Limit) {
        $length = U16 $At; $valueLength = U16 ($At + 2); $valueType = U16 ($At + 4)
        if ($length -lt 6 -or ($At + $length) -gt $Limit) { throw 'Invalid version resource node bounds.' }
        $cursor = $At + 6; $keyStart = $cursor
        while ((U16 $cursor) -ne 0) {
            $cursor += 2
            if ($cursor -ge ($At + $length)) { throw 'Unterminated version resource key.' }
        }
        $key = [Text.Encoding]::Unicode.GetString($bytes,$keyStart,$cursor-$keyStart)
        $cursor = Align4 ($cursor + 2)
        $valueBytes = if ($valueType -eq 1) { $valueLength * 2 } else { $valueLength }
        if ($valueBytes -gt 0 -and ($cursor + $valueBytes) -gt ($At + $length)) { throw 'Invalid version resource value bounds.' }
        $value = ''
        if ($valueBytes -gt 0) {
            $value = if ($valueType -eq 1) { [Text.Encoding]::Unicode.GetString($bytes,$cursor,$valueBytes).TrimEnd([char]0) }
                else { [BitConverter]::ToString($bytes,$cursor,$valueBytes) }
        }
        if ($key -eq 'ProductVersion') {
            if ($value -cne $ExpectedProductVersion) { throw 'Unexpected Windows ProductVersion.' }
            $script:productVersionFields++
            $value = '0.2.0'
        }
        $cursor = Align4 ($cursor + $valueBytes)
        $children = @()
        while (($cursor + 6) -le ($At + $length)) {
            $childLength = U16 $cursor
            if ($childLength -eq 0) { throw 'Unexpected zero-length child.' }
            $children += Read-VersionNode $cursor ($At + $length)
            $cursor = Align4 ($cursor + $childLength)
        }
        [pscustomobject]@{Key=$key;Type=$valueType;Value=$value;Children=$children}
    }
    $tree = Read-VersionNode $valueAt ($valueAt + $valueSize)
    if ($script:productVersionFields -ne 1 -or $tree.Key -ne 'VS_VERSION_INFO') { throw 'Expected one Windows product version field.' }
    [pscustomobject]@{TypeId=$ids[0];NameId=$ids[1];LanguageId=$ids[2];CodePage=$codePage;Reserved=$reserved;Tree=$tree}
}
