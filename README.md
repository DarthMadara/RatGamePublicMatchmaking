# Rat Game Public Matchmaking

**Unofficial Rat Game mod by Madara** · **v0.2.0** · Windows x64 / Steam

Adds opt-in **Public Hosting**, a native-style **Public Lobbies** browser, and host-only moderation (**Kick**, persistent host-local **Ban**, and **Unban**) to Rat Game.

## Download

**[Download v0.2.0 from GitHub Releases](https://github.com/DarthMadara/RatGamePublicMatchmaking/releases/tag/v0.2.0)**

Choose the attached **`RatGamePublicMatchmaking-v0.2.0.zip`**. **Do not use GitHub's automatically generated “Source code” ZIP as an installer.**

[Previous v0.1.0 beta release](https://github.com/DarthMadara/RatGamePublicMatchmaking/releases/tag/v0.1.0-beta.1) remains available.

## Features

- Opt-in **Public Hosting**, OFF by default on a fresh installation.
- **Public Lobbies** browser with player counts, Join, Refresh, and approximately 30-second automatic refresh.
- Only explicitly advertised waiting lobbies are listed; public listings are withdrawn when matches begin.
- Host-only **Manage Players** screen showing connected players and host status.
- **Kick** a selected player without banning them.
- **Ban** a player with confirmation, persist that host-local ban, and reject future gameplay connections by the banned Steam identity.
- **Manage Bans** and individually **Unban** players.
- Moderation is available to hosts even with Public Hosting OFF; ordinary private/friends game flows remain intended to work normally.
- Uses Rat Game's existing installed UI assets and components; no generated art or other game assets.

## Installation and upgrade

**Requirements:** Windows x64 Steam Rat Game, checked against game build **26403247**, and **BepInEx 5.4.23.5 Windows x64**. Other platforms, game builds, and loader versions are unverified. The **v0.2.0 ZIP contains the plugin, not a bundled BepInEx loader**.

1. **Close Rat Game** completely. In Steam, use **Rat Game → Manage → Browse local files** to open its installation directory.
2. **First-time installation:** Install [BepInEx 5.4.23.5 Windows x64](https://github.com/BepInEx/BepInEx/releases/tag/v5.4.23.5) into the Rat Game folder, following the loader's instructions. If BepInEx is already installed, do not overwrite another working loader.
3. Download and extract the **v0.2.0 release ZIP** to a temporary folder.
4. Back up any old `RatGamePublicMatchmaking.dll` outside `BepInEx/plugins`.
5. Copy `BepInEx/plugins/RatGamePublicMatchmaking/RatGamePublicMatchmaking.dll` from the ZIP to the corresponding location in Rat Game's installation directory, replacing the previous mod DLL if present.
6. **Keep existing configuration and banlists.** The ZIP does not contain replacements for them. Ensure there is only one installed copy of this mod's DLL under `plugins`.
7. Start Rat Game **from Steam**. Look for **Public Hosting**, **Public Lobbies**, **Manage Players**, and **Manage Bans** where applicable.

See [INSTALL.txt](INSTALL.txt) for detailed instructions and [UNINSTALL.txt](UNINSTALL.txt) for removal guidance.

## Using the mod

Enable **Public Hosting** and host normally to advertise a waiting lobby. Other mod users can join through **Public Lobbies**. The host can use **Manage Players** to Kick or Ban a remote player and **Manage Bans** to Unban accounts. A Kick does not create a ban; bans are local to the host, not global across Rat Game.

## Testing and known limitations

**November 22, 2026:** During live multiplayer testing with Eric, Kick, Ban, and Unban were reported working as intended on two Steam accounts. An unrelated third player discovered the public lobby and joined for two games — the project's first reported organic public matchmaking session.

These participant-confirmed observations do **not** establish exhaustive compatibility. Host-restart ban persistence, every invitation/reconnect scenario, simultaneous network races, full bystander isolation, physical controller navigation, and other game/mod versions remain unverified. The published 0.2.0 DLL differs from the tested development DLL only in version metadata and startup version text according to release preparation checks; the stamped stable build itself was not separately live-tested.

Release preparation reported a warning-free build, **176 offline regression checks**, **25 harness checks**, **30 Harmony target validations**, and **58 package checks**. Offline checks are not substitutes for live multiplayer testing.

Please report issues with game build, mod version, reproduction steps, and relevant logs, after removing any private information. Historical beta results are documented in [Validation/TEST-SUMMARY.txt](Validation/TEST-SUMMARY.txt); see the [v0.2.0 release notes](https://github.com/DarthMadara/RatGamePublicMatchmaking/releases/tag/v0.2.0) for the current version.

## Source code and building

- [C# source and project](Source/RatGamePublicMatchmaking/src/)
- [Offline tests](Source/RatGamePublicMatchmaking/tests/)
- [Build instructions](Source/RatGamePublicMatchmaking/BUILDING.txt)
- [License](Source/RatGamePublicMatchmaking/LICENSE.txt)

**Source synchronization note:** The repository's existing source and historical test files may still reflect the v0.1.0 beta. The locally prepared v0.2.0 source changes have not yet been confirmed as committed to this branch; the published release ZIP is the current v0.2.0 download. Do not assume the default-branch source reproduces v0.2.0 until it is synchronized.

## AI Usage Disclosure

OpenAI Codex assisted with programming, debugging, and code review for this mod. **No AI-generated artwork, textures, 3D models, audio, or other game assets were created or used.** The mod reuses UI elements and assets from the player's existing Rat Game installation; those game assets are not newly generated or redistributed as original artwork.

## Credits and disclaimer

Created by **Madara**. Rat Game is developed by **Sokpop Collective**. This is an unofficial community modification **not affiliated with or endorsed by Sokpop Collective**. No proprietary Rat Game assets or game assemblies are included in the mod package.

Original mod code is under the [MIT License](LICENSE.txt). Third-party components retain their own licenses.
