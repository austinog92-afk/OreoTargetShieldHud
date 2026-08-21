# Oreo Target Shield HUD

A client-only Pulsar plugin for Space Engineers 1. It reads your ship's current
WeaponCore focus, obtains that entity's real Defense Shields charge/capacity through
the full mod API, and renders a compact Text HUD API overlay. It can also show live
damage dealt to the selected target and shield bars over nearby WC threats.

No server plugin or mod is required. WeaponCore and Defense Shields must already be
present on the server. Text HUD API is enabled locally through Pulsar.

## What it displays

- exact current and maximum target shield HP
- shield percent and an FPS-style color bar
- target name and distance
- live damage dealt to the selected target, split into shield and hull/block damage
- floating current/maximum shield bars for up to eight nearby WC threats within 15 km
- highest maximum shield ever observed for that NPC name, saved locally

The selected target polls at about 5 Hz and nearby shield values at about 2 Hz. The
plugin performs no terminal/grid scans and uses no per-projectile monitor or firing
callback. WeaponCore's aggregate damage callback is registered only while the HUD is
enabled and a selected WC target exists, then it is immediately filtered to your
controlled grid and that target.

## Build (Pulsar Legacy / Space Engineers 1)

1. Install Visual Studio 2022 with **.NET desktop development** and the .NET Framework
   4.8.1 developer pack.
2. Open `OreoTargetShieldHud.csproj` and build **Release / x64**.
3. If Steam is not detected, pass the Bin64 folder explicitly:

   ```powershell
   msbuild .\OreoTargetShieldHud.csproj /p:Configuration=Release /p:Bin64="D:\SteamLibrary\steamapps\common\SpaceEngineers\Bin64"
   ```

   The project checks `SPACE_ENGINEERS_BIN64`, Steam's registered install location,
   and the default Steam library. The value must point to the folder containing
   `Sandbox.Common.dll`, not to Pulsar or the Space Engineers root folder.

4. In Pulsar, open the local-plugins folder from its plugins/developer menu.
5. Copy `bin\Release\OreoTargetShieldHud.dll` into that folder, enable it, and restart
   Space Engineers.
6. Enable **Text HUD API** (Workshop `758597413`) as a client-side mod in Pulsar.

## Chat commands

Commands are intercepted locally and are not sent to server chat.

| Command | Action |
|---|---|
| `/oshield` | Toggle overlay |
| `/oshield on` / `/oshield off` | Explicit overlay state |
| `/oshield bars` | Toggle nearby enemy shield bars |
| `/oshield bars on` / `/oshield bars off` | Explicit nearby-bar state |
| `/oshield bars min` | One-line name, segmented bar, and percent; also enables bars |
| `/oshield bars full` | Two-line full name, percent, current HP, and maximum HP; also enables bars |
| `/oshield resetdamage` | Reset the current target's live damage counters |
| `/oshield pos -0.34 0.82` | Set screen coordinates (-1 to 1) |
| `/oshield scale 0.78` | Set text scale (0.4 to 2.0) |
| `/oshield api` | Show WC / DS / TextHUD connection state |
| `/oshield record` | Show current target's highest observed maximum |
| `/oshield top` | Show five largest locally recorded NPC shields |
| `/oshield save` | Immediately save all maximum-shield records and report the count |
| `/oshield export` | Export all recorded maximum shields to `OreoTargetShieldHud-MaxShields.txt` |
| `/oshield cleardata confirm` | Permanently clear all recorded maximum shields; HUD settings are kept |
| `/oshield help` | Show the short command list |

## Notes

- Records are keyed by the target's displayed grid name. Repeated NPCs with the same
  name continue the same maximum record.
- Generic debris names `Large Grid`, `Static Grid`, and their numbered variants are
  ignored. Old generic-name records are automatically purged when the plugin loads.
- Nearby threat bars also feed the maximum-shield records, so an NPC does not have to
  be your selected focus to update its highest observed capacity.
- Minimal bars are the default. They remove the leading `(NPC-FACTION)` tag and use
  a compact four-to-eight-cell stepped `▁▂▃▄▅▆▇█` shield bar from the programmable-block HUD.
  Bar width uses a logarithmic scale between the weakest and strongest nearby
  shields currently displayed. This keeps small, medium, and capital-class shield
  differences readable even when their capacities are orders of magnitude apart.
  The selected WC target keeps its detailed twenty-four-cell bar in the main panel and
  is omitted from floating bars to avoid a duplicate. Full floating bars scale from
  six to fourteen cells and include exact current/maximum shield HP.
- The record file is client-local; it does not edit the server programmable block or
  its Custom Data. The human-readable export is written beside that record file in
  Space Engineers local storage.
- Maximum-shield records save automatically at most ten seconds after a change and
  when leaving a game session. `/oshield save` forces an immediate write and reports
  how many records are currently stored.
- Damage totals are retained by unique NPC entity ID for the current game session.
  Switching focus and returning to the same ship restores its totals without merging
  different NPCs that share a name. They are not written as permanent encounter
  history. Hull damage is the sum of WC block-damage events, so area damage can
  legitimately count damage to several blocks at once.
- Damage ownership accepts either WeaponCore's firing-player identity or its weapon
  construct entity. This handles cases where WC's construct root differs from the
  controlled cockpit grid while still rejecting other players' damage.
- The floating bars are shield HP bars, not whole-grid structural HP. Space Engineers
  does not expose one reliable combined hull-HP value for a grid. Unshielded threats
  therefore do not receive a floating bar.
- Fortification can increase shield capacity. Because the requested statistic is the
  highest observed maximum shield, a fortified maximum becomes the saved record.
- If the overlay says it is waiting, run `/oshield api`. Verify all three APIs are
  reported ready.

## Publishing through Pulsar

Space Engineers plugins are distributed as public source through GitHub and Pulsar's
PluginHub, not as Steam Workshop items. This repository includes
`OreoTargetShieldHud.xml`, a ready-to-submit PluginHub descriptor. After publishing
the repository, replace its `Commit` placeholder with the full Git commit hash, then
submit the descriptor under PluginHub's `Plugins` folder for review.
