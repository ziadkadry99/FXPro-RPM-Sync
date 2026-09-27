# FXPro RPM Sync — SimHub plugin

(Called "Simagic RPM Sync" before v0.1. On first start it copies the old settings file to the new name.)

Keeps a Simagic wheel's rev lights (built and tested on an **FX Pro** on an Alpha EVO base) matched to the car being
driven, in every game SimHub supports. SimHub supplies the car/RPM data; the plugin pushes per-car LED settings into
**SimPro Manager 3** through its undocumented local API, and SimPro sends them to the wheel.

The FX Pro has **no native SimHub support** (SimPro 3.2.1's "SimHub via base CANFD" is Zeus wheels only), which is
why this goes through SimPro.

## Build / deploy

```
dotnet build -c Release            # builds net48 DLL and copies it into C:\Program Files (x86)\SimHub\
dotnet build -c Release -p:DeployToSimHub=false   # build only
```

- **SimHub must be closed** to deploy (it locks the DLL). The copy step uses `ContinueOnError`, so check with
  `cmp "C:/Program Files (x86)/SimHub/User.FXProRpmSync.dll" bin/Release/net48/User.FXProRpmSync.dll`.
- References SimHub's own DLLs (`Private=False`); `SIMHUB_INSTALL_PATH` overrides the default path.
- SimHub asks to enable the plugin on first start. Log lines are prefixed `[FXProRpmSync]` in `SimHub\Logs\SimHub.txt`.
- Settings persist in `SimHub\PluginsData\Common\FXProRpmSyncPlugin.GeneralSettings.json` (SimHub rewrites it
  on exit, so edit it only while SimHub is closed). Car data cache: `SimHub\PluginsData\Common\FXProRpmSync\`.

## Files

| File | What |
|---|---|
| `FXProRpmSyncPlugin.cs` | SimHub plugin + settings. DataUpdate detects car changes; a background worker does all HTTP. Overrides API, SimHub actions/properties, preset capture/restore. |
| `SimProClient.cs` | SimPro local API client. |
| `DashSwitcher.cs`, `DashCatalog.cs`, `DashSection.cs` | Dash per car: switching/learning logic, SimPro's dash names + preview pictures, settings section. |
| `SimGameFeed.cs`, `SimProTelemetry.cs`, `SimHubFeedMapper.cs`, `FeedSection.cs`, `SimGameStub/` | Dash values from SimHub: SimPro "SimGame" shared memory + simgame.exe stub, struct layout, SimHub -> struct mapping, settings section. |
| `CarLedDatabase.cs` | Lovely Car Data download/cache, exact + series/team matching. |
| `RpmLayout.cs` | `RpmLayout` (15 LEDs in real RPM + flash + optional per-gear), `CarOverride`. |
| `RpmLightsMapper.cs` | RpmLayout ⇄ SimPro `rpm_lights` JSON, percent rounding, palette snapping, fingerprints. |
| `LedPatterns.cs` | Built-in fallback patterns, color schemes, palette. |
| `SettingsControl.cs`, `OverridesSection.cs`, `LedStrip.cs` | Settings UI (WPF built in code, no XAML) with animated LED previews. |
| `Resources/menu-icon.png` | SimHub menu icon (embedded resource). Traced from Simagic's product photo; `assets/fxpro-outline.svg` is the vector. |

## Releases

GitHub: https://github.com/ziadkadry99/FXPro-RPM-Sync (GPL-3.0). CI can't build it because SimHub's DLLs aren't
redistributable, so releases are built locally: bump `<Version>` in the csproj, `dotnet build -c Release
-p:DeployToSimHub=false`, zip `User.FXProRpmSync.dll` + `README.md` + `LICENSE` as `FXProRpmSync-vX.Y.zip`, and upload
it with `gh release create vX.Y`.

## Pipeline (per car change)

1. `DataUpdate` builds a `Target` (game, CarId, SimHub max RPM / redline); worker picks it up.
2. Read the selected SimPro preset's `rpm_lights`; get/capture the **original** (see "Preset capture").
3. Scale max = **SimPro's game max RPM** (`game_get_running_list` → `maxCarSpeed`), fallback SimHub's.
4. Base layout (real RPM):
   - car in Lovely Car Data → `RpmLayout.FromProfile` (real pattern/colors/shift point; per-gear only if the
     wheel supports it);
   - else → the chosen fallback style (`RpmLightsMapper.FromStyle`): generated pattern or the preset's own pattern,
     shift point = SimHub's redline (a guess: 95% of max unless set in SimHub Car Settings), no flash by default.
5. Per-car override (`CarOverride.Apply`): Offset (shift everything ±rpm), Custom (15 LEDs + flash set by hand),
   or Pattern (a fallback pattern for this car, keeping the car's own shift point).
6. `RpmLightsMapper.ToSimPro` → `preset_set_dev_config` (applies live; not saved into the preset).
7. On SimHub exit (or "Restore"), the original preset lights are pushed back.

## SimPro Manager 3 local API (reverse engineered, SimPro V3.2.2)

- `POST http://127.0.0.1:4010/simpro/api/v3/<method>`, JSON body, **no auth**. Response `{status, message, result}`.
- The UI is a CEF web app served from the same server; method names are an enum in `/simpro/js/main.*.js`
  (`const ye={GET_DEVICE_LIST:"get_device_list",...}`), called via `je(ye.X, params)`. Grep that bundle to find
  how the UI calls anything (use Python, not grep: it's one 2 MB line).
- Useful methods: `get_device_list`, `preset_get_selected_dev_config` / `preset_get_dev_config_list` /
  `preset_get_dev_config` / `preset_set_dev_config` / `preset_save_dev_config`, `game_get_running_list`,
  `get/set_game_auto_switch_preset`, `dash_get_list` / `dash_add` / `dash_modify` / `dash_apply` / `dash_preview` /
  `get_dev_dash_page` / `get_dash_category` / `get_dash_device_size`.
- Device ids: `{device_uuid, product_uuid}` from `get_device_list` (FX Pro: `0000000002030000`, `old_device: true`).
- `preset_set_dev_config {device_uuid, product_uuid, preset_uuid, part_type:"rpm_lights", part_id:1, config}` sends
  one part live to the wheel.
- **Read-back lags writes** (~1 write / ~1 s): an immediate `preset_get_selected_dev_config` can return the
  previous state. Never compare right after a set. (The plugin keeps a history of pushed fingerprints for this.)
- `game_get_running_list[].maxCarSpeed` is actually the **current game max RPM** (UI label "Current Game Max RPM"),
  updated per car, sometimes a moment after the car loads (the plugin re-checks every 3 s).

## rpm_lights model and FX Pro behaviour (all verified on the wheel)

```
rpm_lights["1"]: { max_rpm_source (0 game / 1 custom), selected_mode (0 % / 1 RPM), rpm_mode (0 default / 1 per-gear),
                   lights: [ { mode:"X" | "R","N","1".."10", value[15], color[15], max_rpm,
                               redline{...}, rpm_redlines[ stage... ] } ], lights_rpm: [...] }
```

- **Threshold scale:** `value[i]` is on a fixed 0..20000 = 0..100% scale. The FX Pro lights LED i when
  `rpm >= value[i]/20000 * (SimPro's game max RPM)`. `max_rpm` / `max_rpm_source` are **ignored** by the FX Pro.
- **Whole percents, truncated:** the wheel resolves thresholds to whole percents, rounding down (72.7%→72%,
  84.85%→84%, 96.9%→96%, measured). `ToWheelPercent` snaps to the nearest whole % (multiples of 200).
- **Palette only:** LED colors must be SimPro's palette (`#ff0054` red, `#ff6c00` orange, `#fffd51` yellow,
  `#00ff84` green, `#00fffc` cyan, `#0006ff` blue, `#6000ff` purple, `#eeeeee` white, `#000000` off). Any other hex
  displays as **off**. `ToSimProColor` snaps by hue.
- **Redline/flash = `rpm_redlines` stages**, not `redline`. The stage list drives the wheel; each stage's `enabled`
  flag is ignored (a stage in the list is active). `redline` is kept in sync only for SimPro's UI. Stage effect
  `mono` = solid, `breath` = blink with `interval` (units ≈ 50 ms is a guess, not verified).
- **No per-gear, no RPM mode on old devices:** SimPro strips `selected_mode:1` / `lights_rpm` for the FX Pro, and
  its old-device UI has no per-gear mode. The plugin only sends per-gear curves when `old_device` is false (untested).
- **Live per-gear curves (`LiveGearCurves`, on by default):** for cars whose data differs per gear, the plugin builds
  one `rpm_lights` part per gear and pushes the current gear's on every gear change (~1 ms per call; works well on
  the wheel, verified with the iRacing Porsche 992.2 Cup). All gear parts are registered as our fingerprints.
  Without it the "most common" curve is used, which for such cars is the R/N curve (wrong in every driving gear).
  A 0 in a gear's curve means lit from idle (Cup car 1st gear), not unused.
- Presets hold percentages; SimPro supplies each car's max underneath. Official presets are per car for ACC/iRacing.

## Car data: Lovely Car Data

- https://github.com/Lovely-Sim-Racing/lovely-car-data (CC BY-NC-SA 4.0; attribution shown in the UI). Downloaded
  at runtime (manifest cached 24 h, car files 7 days); no dependency on the Lovely SimHub plugin.
- Format: `ledNumber`, `redlineBlinkInterval` (ms), `ledColor[N+1]` (`#AARRGGBB`; index 0 = redline color),
  `ledRpm[0][gear][N+1]` (index 0 = redline/shift RPM). `simId` = SimHub `GameName` lowercased; `carId` = SimHub CarId.
- **Series matching** (LMU names cars by team/entry, e.g. `LMP2_DKR Engineering 2026_3`): if no exact match, use
  entries of the same series prefix (`LMP2` vs `LMP2_ELMS`) only if they all share one setup; else same team only if
  those agree. Never guesses between different setups.
- Data quality varies. Known bad entry: AMS2 McLaren 720S GT3 Evo (shift 7333 in data vs ~7600 in game; its values
  are suspiciously round). Other AMS2 cars sit at 92–98% of max. Use a per-car override for such cases.
- The database has only the shift point, no limiter flash. A generic "97% of max = red" rule was tried and removed:
  the user wants nothing that isn't real per-car data.

## Preset capture / restore

The plugin never saves into the SimPro preset. On first sight of a preset it stores that preset's original
`rpm_lights` (per `preset_uuid`) in settings, builds every car from it, and restores it on exit. If the preset's
current lights differ from both the original and anything the plugin pushed recently, the user edited it in SimPro
and it's re-captured ("Re-capture preset" forces this). Pushing test values by hand while the plugin runs makes the
plugin re-capture them as "original" — restore the real original before experimenting.

## Measuring instead of guessing

- The user reading the in-game RPM HUD lags ~3%; don't trust it for calibration.
- SimHub's local web API gives live telemetry: `GET http://localhost:8888/api/getgamedata` → `NewData.Rpms`,
  `MaxRpm`, `CarId`, `CarSettings_*`. Calibrate by setting all LEDs to one threshold, recording `Rpms` at 20 Hz,
  and having the user hold the throttle at the point where the LEDs flicker; the dwell point is the real threshold.

## User preferences

- Everything automatic; no per-car setup required (per-car overrides are optional).
- Only real per-car data; no guessed flashes or stages.
- Keep the UI visual (animated previews).
- Free, non-commercial plugin; may be shared publicly.

## Dash per car (DashSwitcher)

Verified on the wheel (2026-09-26):
- `preset_set_dev_config` with `part_type:"screens"`, `part_id:1`, `config` = the whole `screens["1"]` object applies
  live: SimPro sends the rotation to the wheel right away (`91 06` dash list via the base).
- **The wheel ignores `active_dash` and shows the first entry of `selected_dashs`.** So the plugin sends the preset's
  rotation rotated to start at the car's dash (or with it added in front, max 10 as in SimPro's UI); the dash button
  keeps cycling the same dashes.
- `get_dev_dash_page {device_uuid, product_uuid}` → `{dash_id}` follows the wheel, including dash-button presses.
  Button presses don't change the preset's `screens` part, so they never trigger a re-capture.
- Dash ids are the wheel's screen page numbers 0-37; names/pictures from `old_offical_dash_list.json` +
  `offical_dash\screen\*.png` (`dash_get_list` returns the same with image URLs). Id 7 "BaseSettings" is the FFB
  settings screen: never learned, not offered.
- Each push makes the wheel save its config page to flash (same as a dash-button press), so pushes only happen when the
  wanted rotation differs from the last one sent.

Behaviour: on a car change, the car's saved dash (per "Game | CarId", `Settings.CarDashes`) is shown; for cars without
one nothing is sent, so the wheel stays on its current dash (user preference). If the selected preset changed since
our last push, SimPro has sent its own rotation and is in charge: the plugin drops its state (nothing to restore).
The wheel is only switched on a car change or when the current car's saved dash changes (not on other re-applies
such as settings saves or SimPro's late max RPM), and not at all when `get_dev_dash_page` already shows the dash. Learning: the worker polls `get_dev_dash_page` every 1.5 s; a change while
driving that isn't ours (4 s quiet time after a push) is saved for the car as *learned*. Dashes *picked* on the
settings page (or with the `KeepWheelDashForCurrentCar` action) are never overwritten by learning. 4 s after a push
the wheel is checked once more and the dash re-sent if something else (e.g. SimPro's per-game preset auto switch)
replaced it. Originals per preset in `Settings.ScreensOriginals`, restored with the rev lights (exit / Restore /
disable).

## Dash values from SimHub (SimGame feed)

Replaces SimPro's game telemetry with SimHub's data (option, off by default). Traced end to end; details and evidence
in `E:\Development\FXProDashes` (README "Wire format", docs/firmware-notes.md).

- **Route:** SimPro's built-in SimGame source. While a process named `simgame.exe` runs, SimPro treats it as the game
  and memcpy's `_STelemetryData` (3136 bytes) from the shared memory `/simgame` (`SimgameReader::pollData`). The
  plugin creates the mapping *before* starting the stub (SimPro runs elevated), writes the struct on every SimHub
  `DataUpdate`, and runs `simgame.exe` (tiny net48 x86 exe from `SimGameStub/`, embedded in the DLL, extracted to
  `PluginsData\Common\FXProRpmSync\`, exits with SimHub).
- **SimPro selects a game only when none is selected** (`STelemetryManager::serviceThread`, every 2 s: first running
  game in its supported list, then kept until that process exits). So SimGame must run before the game starts; if
  the game was already selected, SimPro keeps reading it (the settings page shows SimPro's running game).
- **Observed with SimPro 3.2.2 (2026-09-27):** SimGame works (demo verified on the wheel). Restarting SimHub
  mid-game killed the stub, SimPro switched to the running game (AMS2), and after the game closed SimPro did **not**
  go back to SimGame, not even after the stub restarted. Recovered only after a full SimPro restart (backend
  processes, not just the window) plus a wheelbase restart. `game_get_running_list` never shows SimGame (not in
  SimPro's game list), so "no game" is what it reports while SimGame is in use.
  Hence the stub **outlives SimHub restarts** (runs while any SimHubWPF runs, exits 30 s after SimHub is gone; the
  restarted plugin adopts it) and the plugin only kills it when the option is turned off. On SimHub exit the plugin
  writes a zeroed struct (dash idle) and leaves the stub running.
- **Side effects while on:** SimPro sees SimGame, so its per-game preset auto switch doesn't fire and everything it
  drives from game telemetry (dash, rev lights via `maxRpm`, telemetry effects) uses SimHub's data. FFB unaffected.
- **Mapping:** SimPro's `_STelemetryData` mirrors SimHub's data model (same field names, down to SimHub internals),
  so fields map by name (`SimHubFeedMapper`). Units/ranges, verified in SimPro 3.1.1 (`TelemetryData_v2tov1`,
  `packetTelemetryHigh/Low`) and wheel firmware 1.3.11 (widget senders):
  - SimHub converts pressure / temperature / fuel to the user's display units (`TyrePressureUnit` Psi/Kpa/Bar,
    `TemperatureUnit` Celcius/Fahrenheit/Kelvin, `FuelUnit` Liters/Gallons); the struct needs **psi, °C, litres**
    (the wheel converts for display from SimPro's `su/tu/pu/fu` settings). `Computed.Fuel_LitersPerLap` is also in the
    user's fuel unit.
  - **Gaps:** struct ints in **hundredths of a second**, positive. Wheel: `gapa` = value/100 `"%.1f"`, `gapb` =
    value/100 `"-%.1f"`, u16 (0-655.35 s). SimPro takes `abs(gapBehind)`. Race: SimHub `GaptoPlayer` of the cars one
    position ahead/behind (class filter by `CarClass`); other sessions: `RelativeGapToPlayer` of the nearest cars.
  - Delta `gainLoss`: seconds, + = slower, x100 int16; wheel `"%+0.2f"`, gain/loss bars over ±5 s.
  - Clutch: % pressed in both SimHub (100 - raw engagement) and SimPro's readers; the wheel draws `cl` as 100 - value.
  - Tyre wear: % remaining (100 = new) in SimHub; wheel `"%d%%"`. Fuel per lap: `fuelPerLap` = L/lap x10 (byte).
  - Widths: speed 9 bits, rpm 15 bits, fuel/bias/tyre pressure x10 in 10 bits (max 102.3), brake temps 10 bits,
    tyre/water/oil temps and oil pressure bytes, ABS level and ERS mode low nibbles, lap times ms.
  - SimPro bug: corner 1's middle tyre temperature is sent as corner 0's.
  - Game-specific (raw) values: ACC `Graphics.TCCut`; iRacing `Telemetry.dc*` (TC2, ARB, engine braking, diff, TPS,
    MGU-K deploy mode), plus per-field SimHub property overrides.
- **Demo** (`DemoCar.cs`, ported from FXProDashes with gaps fixed to 1/100 s and every field animated): runtime-only
  switch; a 50 Hz timer steps the simulated lap and writes the struct, while `DataUpdate`'s SimHub mapping pauses.
- Not yet checked on the wheel: SimPro's display name for SimGame (the settings page shows whatever
  `game_get_running_list` returns).

## Dashes on the FX Pro screen (first investigation)

Superseded for custom dashes: the screen image is a TJC `.tft` that has since been decoded, mapped and modified
offline; see `E:\Development\FXProDashes\docs\findings.md`.

- **The FX Pro's dashes are built into its screen firmware.** SimPro only sends the rotation and live telemetry
  (quitting SimPro leaves the dash visible and cycleable; SimPro writes ~100 KB/s in-car, not video).
- The rotation is the preset's `screens` part: `screens["1"] = {active_dash:"20", selected_dashs:["20","36","17","24","21"]}`,
  pushed to the wheel via `setWheelDashPage` during preset sync. Ids are from
  `%LOCALAPPDATA%\SIMAGIC\Simpro3\config\dash\offical_dash\old_offical_dash_list.json` (numeric ids 0-37, no files;
  this is what `dash_get_list` returns for the FX Pro). **Possible feature:** switch dashes per car class through
  this part, the same way the plugin does rev lights (untested).
- Screen firmware `firmware\wheel\fx_pro_screen\FXPproXScreen_App-V1.3.11.0-00000000.sfu` (9.2 MB): `SIMPROSFU100`
  header + fully encrypted payload. Custom dashes would need firmware decryption/modification/reflash (brick risk).
- The *other* dash system in SimPro 3 (`offical_dash_list.json`, encrypted `.sdash` = AES-encrypted web apps; CryptoPP
  Rijndael in simpro3.exe; symbols in `bin\simpro3.pdb` under `biz/sdash`) is for newer dash devices (RaceOS, Mark X,
  MagicDash). Their dashes are React web pages, 800x480, polling telemetry over local JSON-RPC (SimPro 2 used
  `POST http://127.0.0.1:56789/jsonrpc` `{"method":"query","params":{"items":["game_data","axes"]}}`).
  SimPro 3's dash designer (`tools → dash`) exists but is disabled; `dash_add` is a no-op stub in V3.2.2.
- Backups made before experimenting: session scratchpad `dash\backup\` (SimPro config + storage). Nothing was changed.
