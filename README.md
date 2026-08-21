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
- selected target's current and highest-observed hull integrity with a second HP bar
- target name and distance
- live damage dealt to the selected target, split into shield and hull/block damage
- RPG-style floating damage numbers above hit NPCs: white for shield and red for hull
- floating current/maximum shield bars for up to sixteen on-screen WC threats within 15 km
- rolling shield drain/recharge rate with estimated time to break or full charge
- highest maximum shield ever observed for that NPC name, saved locally
- when the selected WC target matches the Roacher script's TRACK target: live T3
  recovered, expected maximum, average, best, lifetime total, and tracked count

The selected shield target polls at about 5 Hz, nearby shield values at about 2 Hz,
and only the selected grid's hull integrity at about 1 Hz. The plugin performs no
repeated inventory scans and uses no per-projectile monitor or firing callback.
The optional Roacher link discovers `RoachDataLCD` and the Roacher programmable
block once, then samples their already-generated TRACK/T3 records at only 1 Hz.
WeaponCore's aggregate damage callback is registered only while the HUD is enabled
and a selected WC target exists, then it is immediately filtered to weapons fired by
your controlled grid. Per-target totals remain keyed to the identified hit entity.

Rapid-fire and multi-projectile damage is combined into short 0.2-second batches
before drawing. This keeps MAC subprojectiles and automatic weapons readable and
caps the HUD at ten simultaneous damage numbers without adding another callback.

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
| `/oshield bars min` | Compact segmented bar and percent beside the WC target tag; also enables bars |
| `/oshield bars full` | Two-line bar, percent, current HP, and maximum HP beside the WC target tag; also enables bars |
| `/oshield names` | Toggle the plugin's NPC names; useful when WC labels are hidden |
| `/oshield names on` / `/oshield names off` | Explicit floating-name state |
| `/oshield resetdamage` | Reset the current target's live damage counters |
| `/oshield pos -0.34 0.82` | Set screen coordinates (-1 to 1) |
| `/oshield resetpos` | Restore the default HUD position |
| `/oshield scale 0.78` | Set text scale (0.4 to 2.0) |
| `/oshield api` | Show WC / DS / Roach T3 / TextHUD connection state |
| `/oshield record` | Show current target's highest observed maximum |
| `/oshield top` | Show five largest locally recorded NPC shields |
| `/oshield save` | Immediately save all maximum-shield records and report the count |
| `/oshield export` | Export all recorded maximum shields to `OreoTargetShieldHud-MaxShields.txt` |
| `/oshield cleardata confirm` | Permanently clear all recorded maximum shields; HUD settings are kept |
| `/oshield help` | Show the short command list |

## Notes

- Records are keyed by the target's displayed grid name. Repeated NPCs with the same
  name continue the same maximum record.
- Roach T3 integration is automatic when the controlled ship has an LCD named
  `RoachDataLCD` and the Oreo Roacher PB. The plugin reads the PB's existing
  `T3STAT`/`T3RUN` records and the LCD's live `TRACK:` line; it never scans cargo.
  The T3 card appears only when that TRACK target matches the current WC target.
- Generic debris names beginning with `Large Grid` or `Static Grid`, including
  numbered or formatted variants, are excluded from the selected-target display,
  nearby bars, and saved records. Old generic-name records are automatically purged
  when the plugin loads.
- On-screen nearby threat bars also feed the maximum-shield records, so an NPC does
  not have to be your selected focus to update its highest observed capacity.
- Nearby bars prioritize visible threats before using one of the sixteen slots.
- Shield rate is calculated from the existing selected-target samples. A maximum-
  capacity change resets the rate baseline, preventing fortify/unfortify transitions
  from appearing as shield drain or recharge.
- Minimal bars are the default. WeaponCore already supplies the NPC name, so floating
  bars only draw a compact four-to-eight-cell stepped `▁▂▃▄▅▆▇█` shield bar and percent.
  They sit just above and to the right of the WC tag, move farther right for longer names,
  and flip to the left near the screen edge.
  Run `/oshield names on` when WeaponCore labels are hidden; this brings back the compact
  plugin name and centers the complete name/bar tag over the NPC. The setting is saved.
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
  does not expose one precomputed combined hull-HP value for a grid. The selected
  target's hull bar therefore sums the integrity of its existing blocks once per
  second and keeps the highest observed maximum for that entity. Unshielded nearby
  threats do not receive a floating bar.
- A block destroyed before the plugin first observes a target cannot be reconstructed
  from client data, so the hull maximum is the highest total observed after selection.
  Once observed, destroyed blocks do not shrink that target's cached maximum.
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
