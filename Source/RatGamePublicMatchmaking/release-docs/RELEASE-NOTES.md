Host moderation is now available in Rat Game Public Matchmaking v0.2.0 by Madara.

### What's new

- Host-only **Esc / Pause → Manage Players**, with connected Steam names and a
  protected host row.
- **Kick** removes one remote player without banning them.
- **Ban…** requires confirmation, saves a host-local Steam account ban and rejects
  that account's future game connections.
- **Manage Bans** and individual **Unban**; unbanned players join again normally.
- Steam identity-based moderation, independently mapped to Netcode connections.
  No client moderation endpoint, global bans or centralized reporting.
- Native-style UI using the game's installed components/assets. Public Hosting
  remains optional; moderation also works in ordinary private/friends sessions.

Existing Public Hosting, public lobby discovery/refresh and native host/join paths
are retained. No movement, combat, physics, objectives or maps are changed.

### Install / upgrade

Download **RatGamePublicMatchmaking-v0.2.0.zip** below, not the source ZIP.
ZIP SHA-256: `53AC836EE3CEDAF40DFF6214D2E3A645EFEBB16737332D16C50A0384E49F7F2D`.
Requires **Windows x64 Steam Rat Game build 26403247** and **BepInEx 5.4.23.5 x64**.
New users: follow the included **INSTALL.txt** for the official loader prerequisite;
no previous mod beta is required. This ZIP does not bundle BepInEx.

Existing users: quit the game completely, back up the old mod DLL outside
`BepInEx/plugins`, and replace only:
`BepInEx/plugins/RatGamePublicMatchmaking/RatGamePublicMatchmaking.dll`.
Keep your configuration and entire host-local ban directory, including backups.
No configuration or banlist ships in the ZIP. Launch through Steam and check the
fresh startup log for **0.2.0 by Madara loaded**. Both players should use the same
game build and mod release/checksum; mixed versions are not assumed compatible.

### November 22, 2026 — First confirmed organic public matchmaking session

During testing with Eric, an unrelated player discovered the public lobby, joined
successfully and played two games. Madara also confirmed successful **Kick, Ban
and Unban** using two Steam accounts on the corrected development implementation.

The stable DLL changes only version metadata and startup version text. Source,
normalized complete assembly and Windows version-resource comparisons confirm
unchanged implementation. The newly stamped DLL has not had a separate live session.

Release validation passed: **176 offline regression checks**, **25 harness checks**,
**30 exact Harmony targets**, **1,804 resolved external references**, zero build
warnings/errors. Offline checks are distinct from the participant-confirmed live
results. See the enclosed **Validation/TEST-SUMMARY.txt**.

### Known limitations

- Steam lobby membership and game connections are separate. A banned account may
  still occupy a Steam lobby slot even though game admission is rejected.
- Actual host-restart persistence, every invitation/public rejoin route,
  three-account bystander isolation during moderation, all races/transitions,
  controllers, other display settings and unmodified/mixed-version clients remain
  outside the confirmed report.
- Corrupt/unreadable ban storage blocks remote admission rather than resetting
  bans. Close the game and preserve/repair the files deliberately.
- Only the stated game/loader/platform target is validated. Steam results may lag.

See **KNOWN LIMITATIONS.txt** and **UNINSTALL.txt** for details and rollback.
The previous [v0.1.0-beta.1 release](https://github.com/DarthMadara/RatGamePublicMatchmaking/releases/tag/v0.1.0-beta.1)
remains available.

### Credits and AI Usage Disclosure

Created by **Madara**. Rat Game is developed by **Sokpop Collective**. This is an
unofficial community mod, **not affiliated with, approved or endorsed by Sokpop**.
OpenAI Codex assisted with programming, debugging and code review. **No AI-generated
artwork, textures, icons, models, audio or other artistic assets were created or
used.** Native UI/assets are referenced from the installed game at runtime; no
proprietary game assets or original assemblies are redistributed. Mod code is MIT
licensed; dependency notices are included.
