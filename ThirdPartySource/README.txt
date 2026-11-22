Unity Doorstop 4.5.0 corresponding source

UnityDoorstop-4.5.0.zip is the unmodified source archive for the Doorstop version
whose Windows x64 winhttp.dll is bundled with BepInEx 5.4.23.5 in this package.
It accompanies that LGPL-licensed binary; no later source version is substituted.

Upstream:
https://github.com/NeighTools/UnityDoorstop/tree/33dab9a6733862eb81869ff08431d9478b28784b
Source download:
https://github.com/NeighTools/UnityDoorstop/archive/refs/tags/v4.5.0.zip

Archive SHA-256:
7F0C963104AA08BF5FEFEF8FF85E7FECD8306838F5AF3101487D9DB4E9188D63
The archive's Git comment identifies commit:
33dab9a6733862eb81869ff08431d9478b28784b

The archive includes LICENSE, all source modules, Windows proxy generation,
xmake.lua, info.lua and build.bat/build.ps1/build.sh. There are no missing Git
submodules declared by this source tree. The source includes its own notices
for embedded code; preserve them. The full LGPL 2.1 text is also supplied in
../Licenses/UnityDoorstop-LGPL-2.1.txt.

To modify/rebuild on Windows, extract the archive and follow its README.
Use a suitable Windows C toolchain and xmake; the supplied build wrappers
install/bootstrap xmake. Build a release x64 library, for example using the
upstream build.ps1 options -arch x64. Upstream CI/build scripts are included.
Those development tools and Windows SDK/system components are not bundled here.
This package includes the upstream binary; a Doorstop rebuild was not performed
as part of the mod's offline C# regression run.

To try your modified library, fully close Rat Game and back up the current
winhttp.dll, then replace that one library next to Rat Game.exe. Keep its filename
and a compatible Doorstop config. It remains a separate, replaceable DLL; the
mod does not sign, lock or merge it into proprietary game files. An incompatible
build can stop the loader, so restore your backup if needed.

Other bundled dependencies use MIT licenses; their source locations and complete
copyright/permission notices are in ../THIRD_PARTY_NOTICES.txt and ../Licenses.
Keep this source archive and the notices with a redistributed complete bundle.
