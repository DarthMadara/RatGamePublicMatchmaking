# Rat Game Public Matchmaking

**Unofficial Rat Game mod by Madara** · **v0.1.0-beta.1** · Windows x64 / Steam

Adds opt-in **Public Hosting** and a **Public Lobbies** browser to Rat Game's main menu, using the game's existing Steam lobby system and native-style UI.

> **Early beta:** Single-account Steam hosting and lobby lifecycle were checked locally. **Two-account public discovery and joining have not yet been verified.** Please report results rather than assuming public joining is fully tested.

## Download

**[Download v0.1.0-beta.1 from GitHub Releases](https://github.com/DarthMadara/RatGamePublicMatchmaking/releases/tag/v0.1.0-beta.1)**

Use the release ZIP for installation. **Do not download the repository source ZIP as an installer.**

## Features

- Opt-in Public Hosting switch (OFF by default)
- Public Lobbies browser on the main menu, with player counts, Join and Refresh
- Automatic refresh approximately every 30 seconds while the menu is open
- Only advertises explicitly public, waiting lobbies; withdraws listings when matches start
- Rechecks public listing conditions when attempting to join
- Preserves the game's normal private/friends lobby paths

## Installation

Requires the Windows 64-bit Steam version of Rat Game. The beta was locally checked with game build **26403247** and **BepInEx 5.4.23.5**.

1. Close Rat Game. In Steam, right-click **Rat Game → Manage → Browse local files**.
2. Download and extract the **release ZIP** to a temporary folder.
3. **New BepInEx installation:** Copy the *contents* of the ZIP's `GameFiles` folder into the game directory, next to `Rat Game.exe`. Do not copy the `GameFiles` directory itself.
4. **Existing BepInEx installation:** Back up your current setup. If it uses the matching loader version, copy only the mod's plugin directory and supplied configuration file to their corresponding `BepInEx` locations. Do not overwrite another loader or other plugins.
5. Launch through Steam. Look for **Public Hosting** and **Public Lobbies** on the main menu.

Read [INSTALL.txt](INSTALL.txt) for complete instructions, including updates and troubleshooting. See [UNINSTALL.txt](UNINSTALL.txt) to remove the mod.

## Using the mod

Enable **Public Hosting**, then use the game's normal **Host game** action. The lobby is advertised after the host reaches the waiting lobby. Turn Public Hosting OFF to withdraw it. On the main menu, use **Public Lobbies** to find compatible waiting lobbies and select **Join**.

Both players should use the same mod release and game build for beta testing. This is a recommended test setup, not a guarantee of successful remote joining.

## Beta limitations and testing

**Not yet verified:** real two-account public joining, friends/invite late joins, physical controller navigation, and live network races. Steam metadata can be delayed; full or withdrawn lobbies may briefly appear in stale results.

The release passed **100 offline regression checks**, and local Steam hosting/lobby lifecycle checks. These do **not** establish successful remote public joining.

Read [KNOWN LIMITATIONS.txt](KNOWN%20LIMITATIONS.txt) and [Validation/TEST-SUMMARY.txt](Validation/TEST-SUMMARY.txt) for details. Please include your game build, mod version, and reproduction steps when reporting bugs. Review logs for private/session information before sharing.

## v0.2.0 — Host moderation (release preparation)

The next version adds host-only **Manage Players** and **Manage Bans** screens, targeted **Kick**, persistent host-local **Ban**, and individual **Unban**. The UI reuses Rat Game's installed native assets; no AI-generated assets were introduced.

**November 22, 2026 multiplayer milestone:** The mod author and Eric reported successful live tests of Kick, Ban, and Unban. During their session, an unrelated player joined through public matchmaking and stayed for two games — the project's first reported organic public matchmaking session.

**Testing scope:** These are participant-reported results, not an exhaustive certification. Invitation bypass, ban persistence after restart, controller navigation, and all multiplayer race conditions have not been established by these reports. The previous beta remains the downloadable release until a separately packaged v0.2.0 binary is published.

## Source code and building

- [C# source and project](Source/RatGamePublicMatchmaking/src/)
- [Offline tests](Source/RatGamePublicMatchmaking/tests/)
- [Build instructions](Source/RatGamePublicMatchmaking/BUILDING.txt)
- [License](Source/RatGamePublicMatchmaking/LICENSE.txt)

The repository tracks source and documentation; **compiled binaries and bundled loader dependencies are distributed in GitHub Releases**, not the source tree. Building requires your own Rat Game installation and BepInEx references. See the build instructions for the exact commands.

## AI Usage Disclosure

OpenAI Codex assisted with programming, debugging, and code review for this mod. **No AI-generated artwork, textures, 3D models, audio, or other game assets were created or used.** The mod's interface reuses UI elements and assets from the player's existing Rat Game installation to remain consistent with the game's visual style; those assets are not newly generated or redistributed as original artwork.

## Credits and disclaimer

Created by **Madara**. Rat Game is developed by **Sokpop Collective**. This is an unofficial community modification and is **not affiliated with or endorsed by Sokpop Collective**. No proprietary Rat Game assets or game assemblies are distributed as source.

The original mod code is licensed under the [MIT License](LICENSE.txt). Third-party components included in release downloads retain their own licenses; see [THIRD_PARTY_NOTICES.txt](THIRD_PARTY_NOTICES.txt) and [Licenses/](Licenses/).
