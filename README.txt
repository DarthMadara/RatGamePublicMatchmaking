Rat Game Public Matchmaking
First beta: 0.1.0-beta.1 | by Madara

Want to open your rat lobby to more people? This mod adds a Public Hosting
switch and a Public Lobbies list right on the main menu. It uses the game's
own paper panels, fonts and buttons, so it should feel familiar.

This is an early beta. Local hosting and the match/lobby transitions have
been checked, but a real two-account public join has not. Friend/invite late
joins, controller play and real network races have not been verified either.
Please read KNOWN LIMITATIONS.txt before using it.

GETTING STARTED

Open INSTALL.txt and follow the steps for your setup. The included BepInEx
5.4.23.5 loader is for Windows 64-bit Steam Rat Game. The game build used for
local validation was 26403247. No game files, fonts or artwork are included.

HOSTING A PUBLIC LOBBY

Turn Public Hosting ON, then click the game's normal Host game button.
Turning the switch on at the main menu arms your next host; it does not create
a lobby by itself. You can also use the switch in lobby settings or Pause.

Your lobby is listed once the host is waiting in the loaded 3D lobby. Starting
a match withdraws its public listing. Returning to the waiting lobby lists it
again only if you have left Public Hosting ON. Turn it OFF to withdraw it.
Choosing either normal Friends Only / Private visibility arrow also turns
Public Hosting OFF. Private keeps the game's own Steam visibility behavior.

Public Hosting is OFF when using the supplied fresh config. An explicit choice
you make afterward is saved for later, so check the switch before hosting.

FINDING A GAME

Look at Public Lobbies on the right of the main menu. It refreshes automatically
while that menu is open, about every 30 seconds. Refresh checks again sooner.
Each row shows the host/lobby name, current players, maximum players and Join.

The list is limited to nearby, compatible, explicitly public waiting lobbies
with room for another player. Full, withdrawn or unavailable rows cannot be
joined through the mod. You will not see games already in progress. An empty
list can mean there simply are no suitable hosts; try Refresh or host your own.

JOINING

Use Join next to the lobby you want. The mod checks the listing again before
asking Steam to join. A host can start, withdraw or fill up while you are
looking, so an old listing may stop being available. Cancel join is available
while a public attempt is pending. Some timed-out Steam requests must finish
before you can retry; restarting may be necessary for the same lobby.

For this beta, have both people use this same package and the same game build.
That is a recommended test setup, not a promise that public joining works.
The normal Steam/game connection path is still used, and friends/invite paths
are retained in code, but their late-join behavior has not been tested here.

IF SOMETHING GOES WRONG

Launch through Steam, wait for it to be ready and try Refresh. If an update or
another mod causes trouble, close the game and restore your backup. UNINSTALL.txt
has the removal steps. BepInEx\LogOutput.log can help explain a failure; review
it before sharing it, since logs may contain session or player details.

F8 still opens the optional fallback panel. It starts hidden. Development
commands, hierarchy capture and layout-only preview rows are disabled in the
supplied config and are not used for public discovery. Leave NativeUiTools OFF.

Steam updates arrive asynchronously. A host changing state just after the last
client check can still race a connection. This beta does not provide complete
atomic protection. Already connected players are not kicked just for withdrawing
the public listing. More detail is in KNOWN LIMITATIONS.txt.

Thanks for trying it. If you report a problem to Madara, include what you did,
your game build, this beta number and whether the issue repeats after restarting.
This is a community mod, not an official Rat Game or Steam release.

LICENSE AND CREDITS

Madara's mod code is available under the MIT license in LICENSE.txt.
Bundled dependencies retain their own licenses; see THIRD_PARTY_NOTICES.txt.
Matching Doorstop source is included in ThirdPartySource. Mod source and offline
regression tests are included in Source for anyone who wants to look under the hood.

The package is called 0.1.0-beta.1. To preserve the validated binary exactly,
the plugin still identifies itself as 0.1.0+waiting-browser.1 (BepInEx version
0.1.0). Its SHA-256 is listed in SHA256SUMS.txt and RELEASE-MANIFEST.json.
