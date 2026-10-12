# ONI Perf

A frame-rate and load-time mod for Oxygen Not Included. Each of its 21 frame levers takes out work
the game does on every rendered frame whose result nothing reads, or does it at the rate the game
itself was tuned for instead of at the frame rate. Four load levers make loading a large colony
faster, and an optional fifth, `fastLoad`, keeps the window live while it loads. With the default
settings it changes no gameplay: the colony makes the same decisions and the screen shows the same
picture.

On a 200-duplicant, 1,000-building test colony at speed 1, this release with its default settings
took the game from **53.5 fps to 77.2 fps** (+44%). The full set of levers measured during
development, which includes two that ship switched off, took the same colony from **54.9 to
123.8 fps**. Details and method: [The numbers](#the-numbers). A large colony that took about
12 minutes to load took under 2 minutes with the default load levers: [Loading a colony](#loading-a-colony).

- Tested on game build 744825 with Spaced Out active.
- No new buildings, no save data: the mod can be added to or removed from a colony at any time.
- Settings are a `config.json` beside `OniPerf.dll` (below). Without one, the defaults apply.

## Why the game needs it

**The frame is limited by C# work on the game's main thread, not by the simulation and not by the
graphics card.** Split by Unity's player-loop phases at speed 1, a 23.83 ms frame was 20.4 ms of
game scripts in `Update` and `LateUpdate` and 3.11 ms of rendering and presenting, with the GPU
under 5% busy. The simulation itself runs on its own thread: replacing the game's simulation
library with a faster build moved the frame rate by nothing measurable (54.94 against 54.91 fps).
So the frame rate goes up only when the main thread does less.

Much of what the main thread does is repeated per rendered frame although its inputs change far
less often. What the levers found, each measured on the test colony:

- **Duplicant brains run once per frame, but were tuned for 30.** `BrainScheduler` runs a fixed
  number of chore searches per rendered frame; the game's own constants state a 30 fps frame. At
  94 fps it searched 3.2 times as often as its tuning intends. (Rate-limited by `brainRate`, off by
  default, see below.)
- **Property textures are refilled every frame for data that changes per sim tick.** About ten of
  the 17 are rebuilt each frame, and the sim-fed ones upload the whole map, not the visible part,
  while the sim ticks 5 times a second. `propTex`: −2.43 ms at speed 1.
- **Closed screens keep costing.** A screen the player has closed stays alive with its canvases,
  toggles, graphs and scroll views running. After one visit to Consumables, every later frame paid
  +17.95 ms. `screenSleep` puts closed screens to sleep and wakes them on open.
- **Duplicant appearance is re-derived every frame.** Each duplicant's symbol overrides (face,
  clothes) are rebuilt and re-uploaded whether or not anything changed. `symbolMemo`: −0.98 to
  −2.14 ms; `overrideRange`: −0.38 to −0.59 ms.
- **Animations that do nothing are still updated.** Of 646 animation updates a frame, 280 did
  nothing (`parkAnims`), and 73% of the transforms marked as moved were switched-off laser effects
  that every duplicant carries (`markSkip`, −0.20 to −0.40 ms at the median).
- **Cameras render what nothing shows.** The timelapse camera renders every frame though the
  timelapse does not use it; the sim overlay camera renders with no overlay open; the gamepad
  cursor camera renders with a mouse (`idleCams`). Six "lerper" cameras are replaced by six
  blits with bit-identical output (`lerpBlit`, −0.39 ms).
- **Smaller per-frame costs:** Steam Input polled every frame twice over (`steamPoll`, −0.41 ms
  and −0.46 ms); pipe contents redrawn every frame while nothing feeding them changed (`pipeCache`); achievement checks at frame rate
  (`achieveRate`, −0.10 ms); idle trackers and effects (`idleSleep`, −0.31 ms); idle duplicants'
  worker components (`workerSleep`); the UI raycast repeated while the mouse is still
  (`raycastCache`, −0.16 ms); world-space status bars (`barRate`); hover text, the audio mix and
  paused-only work (`hoverCache`, `mixRate`, `pausedSkip`).

Figures marked as a range are from repeated measurements; the levers' effects overlap, so the
individual figures do not add up to the total.

## The levers

Every lever is a `config.json` key. The frame levers all default **on** except two.

| key | default | what it does |
|---|---|---|
| `screenSleep` | on | closed screens' canvases, toggles, graphics, graphs and scroll views switched off until reopened |
| `propTex` | on | property textures refilled on a sim tick or a view change, not per frame |
| `lerpBlit` | on | six camera passes replaced by texture blits with identical output |
| `idleSleep` | on | idle trackers and heat effects switched off until their wake event |
| `steamPoll` | on | Steam Input refreshed at 20 Hz while no gamepad is in use; Steam callbacks at 30 Hz (`steamPollCallbacksHz`) |
| `pipeCache` | on | the gas and liquid pipe contents reused while nothing that feeds them has changed (mostly while paused) |
| `achieveRate` | on | achievement checks at a fixed rate, not per frame (a newly met achievement can register a few seconds later) |
| `idleCams` | on | the timelapse camera off; the sim overlay camera only with an overlay open; the gamepad cursor camera only with a gamepad |
| `workerSleep` | on | idle duplicants' worker and outfit components skip their empty per-frame update |
| `raycastCache` | on | the UI raycast reused while the mouse has not moved |
| `mixRate` | on | the audio mixer's count of visible duplicants taken at a fixed rate, not per frame |
| `hoverCache` | on | the hover card rebuilt only when the cursor, camera, tool, overlay or selection has changed |
| `pausedSkip` | on | two per-duplicant passes (animation event offsets, name-label culling) skipped while paused with a still camera |
| `symbolMemo` | on | duplicant symbol overrides re-derived only when they change |
| `overrideRange` | on | only the changed part of the override data is uploaded |
| `parkAnims` | on | animation updates that would do nothing are parked |
| `markSkip` | on | switched-off effects are not marked as moved |
| `barRate` | on | world progress and health bars stop rewriting a fill change that cannot be seen |
| `cpuFix` | on | the game's core-count query answered without an engine call (see Fast Track below) |
| `brainRate` | **off** | duplicant and critter brains at 30 updates a second of real time (`brainRateHz`), not once per frame |
| `moveHold` | **off** | a walking duplicant stays "moving" between sim sub-ticks instead of flipping to stopped and back |

**Why two are off.** In vanilla, how often duplicants search for chores rises with the frame
rate. `brainRate` holds it at the game's own 30-a-second tuning, so above 30 fps duplicants search
less often than they would in vanilla: a gameplay change, however small. `moveHold` delays a
"stopped" event by up to a tenth of a second. Both are measured and work; they are off because
the defaults promise no gameplay change.

The load levers, described under [Loading a colony](#loading-a-colony):

| key | default | what it does |
|---|---|---|
| `conveyorPaths` | on | the conveyor network rebuild answers its path questions from an index instead of rescanning every kept path (also on every conveyor edit in play) |
| `typeNameCache` | on | the type lookups the state-machine load repeats for every object are cached |
| `smRestore` | on | the state-machine restore reuses the long type-name strings it reads over a million times on a large colony instead of decoding each anew |
| `loadGcHold` | on | the garbage collector is held off while a colony loads, with one collection at the end; only with 24 GB or more installed (`loadGcHoldRamGb` overrides the detected amount) |
| `fastLoad` | **off** | experimental: the load spread over frames behind its own loading screen, so the window never stops responding, and objects far from the camera built after it |
| `conveyorPathsCheck` | off | a test switch: runs the game's own conveyor search beside `conveyorPaths` and logs any difference |

### config.json

The mod never writes this file: a Workshop update replaces the mod folder and would delete it.
Create it next to `OniPerf.dll`, with only the keys you want to change:

```json
{
  "brainRate": true,
  "brainRateHz": 30,
  "steamPoll": false
}
```

A key that is missing keeps its default. A misspelled key or a wrong type is named in
`Player.log` and ignored. A file that cannot be read costs one warning and the defaults. The load
line in `Player.log` lists which levers are on:

```
[OniPerf] loaded 0.3.0.0; levers: 23 of 27 on; off: conveyorPathsCheck, fastLoad, brainRate, moveHold; steamPollCallbacksHz 30; from defaults (no config.json)
```

A lever that cannot find the game code it expects (after a game update, say) logs a warning and
stays off; the rest still load.

## The numbers

**The test colony.** Survival, the base-game Sandstone asteroid (240 × 380), seed 1, built by a
script so it is the same every time: about 1,000 functional buildings, powered and plumbed, and 200
duplicants, across about 40% of the asteroid. The save is a test fixture and is not published.

**The method.** One game at a time on an otherwise idle machine (AMD Ryzen 9 7950X, 64 GB,
Windows), vsync off. Each run loads the colony from the main menu, lets it run 180 s at speed 1
so it settles, then measures a 60 s window at speed 1 and one at speed 3. For the development set, arms were alternated
(vanilla, mod, vanilla, mod) so drift in the machine falls on both. Frame times are taken from
every frame in the window; fps is frames over wall-clock seconds. The game clock held 1.00 and
3.00 game seconds per real second in every arm, so no arm buys frames by running the colony
slower.

**This release, default settings** (no `config.json`; 19 of 21 levers on). Measured in one sitting,
one run per arm, with the game's own simulation library in both:

| | vanilla | this release, defaults |
|---|---|---|
| speed 1 fps | 53.5 | **77.2** (+44%) |
| speed 1 frame p50 | 18.13 ms | 12.71 ms |
| speed 1 frame p95 | 24.51 ms | 17.74 ms |
| speed 3 fps | 32.1 | **37.8** (+18%) |
| speed 3 frame p50 | 30.31 ms | 25.26 ms |

**The development set** (all levers, two runs per arm, measured on an earlier day):

| | vanilla | development set (21 levers) |
|---|---|---|
| speed 1 fps | 54.9 | 123.8 |
| speed 1 frame p50 | 17.86 ms | 8.03 ms |
| speed 1 frame p95 | 22.00 ms | 12.72 ms |
| speed 3 fps | 34.1 | 53.2 |

The development set is not this release: it also had `brainRate` and `moveHold` on, one further
lever that is not in this release, and a rebuilt simulation library that was separately measured
to be worth nothing to the frame rate (54.94 against 54.91 fps). The defaults keep about a third of
the development set's speed-1 gain. How much of the rest `brainRate` and `moveHold` account for
has not been measured on its own.

Both release runs were made with the parallel-marking garbage-collector runtime from
[oni-gc-runtime](https://github.com/Salacious-Oni-Mods/oni-gc-runtime) installed. It shortens
garbage-collection freezes; in these windows one frame of the mod's speed-1 run and one of
vanilla's speed-3 run held a collection.

**To check it on your own colony,** load it with the mod enabled and then disabled, let it run a
few minutes at the same speed with the camera in the same place, and compare the frame rate any
frame-rate overlay (Steam's, for example) shows. Only this one colony has been measured; how much
another colony gains has not been.

## Loading a colony

**What is slow.** The game loads a save in one call on the main thread. Until it returns no frame
is drawn, so Windows marks the window "Not Responding". On a large colony that call is minutes long,
and most of it is not reading the save: it is the garbage collector running again and again over a
heap that is almost all live colony data, the same few hundred long type names decoded and compared
over a million times, and one conveyor-network rebuild that rescans every path for every sink.

**The default load levers** (`conveyorPaths`, `typeNameCache`, `smRestore`, `loadGcHold`) keep the
game's own load and make it do less. The load is just as frozen, only shorter. `loadGcHold` is
most of the gain on a large colony, and it costs memory: what the collector would have freed
during the load stays allocated until the one collection at the end. So it switches itself off on a
PC with less than 24 GB installed, and its log line says so. **Below 24 GB installed, a very large
colony loads about as slowly as without the mod** (the other three levers still apply).

**`fastLoad` (experimental, off by default)** replaces the game's one call with the same steps
spread over frames behind a loading screen of its own, so the window never stops responding.
Objects more than a screen from the saved camera (and on Spaced Out, everything on another
asteroid) are built after the loading screen, at 60 fps while you use the game and 30 fps when you
do not; the game stays paused until they are all built, and a notification shows the progress.
It holds the collector under the same 24 GB rule as `loadGcHold`, so below 24 GB it keeps the
window live but does not make a very large colony load faster. It stays off by default until
three things are checked: mods that read the whole colony when it spawns (the objects built after
the loading screen are not there yet at that moment), running alongside Fast Track, and saves from
each DLC. Turn it on with `"fastLoad": true` if you want the live window now. Three of its files
follow the game's own load code closely and are not under the MIT licence; see `NOTICE`.

**Measured**, on the same PC as below, the game's own simulation library. Each time is from leaving
the main menu to the colony running and ready (for `fastLoad`, after the last far object is built),
not only the game's load call, so it is longer than a load-call time on the same colony. Peak
memory is the process's peak private memory, which includes everything the game holds, not only
the save:

| colony | vanilla | default load levers | `fastLoad` on |
|---|---|---|---|
| test colony (1,000 buildings, 200 duplicants) | 10.4 s, 14.6 GB | 9.8 s, 14.7 GB | 10.1 s, 14.4 GB |
| 'Some light torture' (Spaced Out, 84,000 buildings) | 52.1 s, 14.2 GB | 43.2 s, 18.1 GB | 38.4 s, 15.5 GB |
| 'Euphoria v22.3' (base game, 66 duplicants, 239,000 objects) | about 730 s * | 110.9 s, 28.0 GB | 109.2 s, 27.2 GB |

\* Measured during development on an earlier setup of the same PC; its peak memory was not
recorded. With `loadGcHold` switched off (as on a PC under 24 GB), the Euphoria load was still
running after 10.5 minutes and was stopped there.

On the small test colony neither is faster than vanilla in any way that matters (`fastLoad` 10.1 s
against 9.8 s for the default levers, one run each on different days); the gains show on large
colonies. With the default load levers the window is still frozen for the whole load; with
`fastLoad` on it answers throughout, though not smoothly everywhere: on 'Some light torture' the loading screen took 29.8 s, then 1,321
far objects were built in 7.8 s while the game was already on screen, and the slowest of those
frames took 0.7 s. On Euphoria the total is about the same as the default levers (109.2 s against
110.9 s), but the loading screen is gone after 16.6 s and the other 220,000 far objects are built
over the next 89 s with the colony on screen and paused, the slowest frame taking 0.05 s.

## Alongside other performance mods

**Fast Track** (Peter Han) is the established performance mod and covers different ground: it
moves pathfinding, rooms, conduits, tile textures and other work onto background threads and
trims allocations and UI work across 50 options. ONI Perf does not do any of that; it removes
per-frame repetition on the main thread. The two are meant to combine.

- **Fast Track needs `cpuFix` on this game build.** On build 744825 (Unity 6000.3.5f2), the call
  the game uses to count CPU cores is refused when made from a constructor. Fast Track's job
  manager makes it there, never starts, and the game stalls at about 0.1 fps. `cpuFix` answers that
  call from the .NET runtime instead, which is legal anywhere; with it, Fast Track 0.18.5 ran at
  48.3 fps against vanilla's 41.3 in the same measurement. Leave `cpuFix` on if you use Fast Track.
- **Running both together has not been measured.** Some areas are close enough that they may hook
  the same game code; until that is checked, if something looks wrong with both installed, switch
  off our lever in the matching row first:

| ONI Perf lever | Fast Track option in the same area |
|---|---|
| `propTex` | `ReduceTileUpdates` |
| `pipeCache` | `DisableConduitAnimation`, `CullConduits` |
| `achieveRate` | `DisableAchievements` |
| `parkAnims`, `markSkip`, `symbolMemo`, `overrideRange` | `AnimOpts`, `RenderTicks` |
| `hoverCache` | `InfoCardOpts` |
| `brainRate` | `ChorePriorityMode` |

**`fastLoad` and Fast Track together have not been run.** That is one of the checks it waits on
before it can default on.

**Frame-pacing mods** (Unlock FPS, FPS Limiter, Performance Assist, Controlled Framerate) change
the frame-rate cap and vsync. This mod touches neither, so they do not overlap; running them
together has not been measured.

**Multithreaded simulation mods** replace the simulation library. The simulation was not what
limited the frame rate on the test colony (above), so they and this mod address different costs.

## Install

Subscribe on the Steam Workshop, or copy the `OniPerf` folder (with `OniPerf.dll`, `mod.yaml` and
`mod_info.yaml`) to `Documents/Klei/OxygenNotIncluded/mods/local/` and enable it in the Mods
screen. To remove it, disable or delete it; nothing is left behind in your saves.

## Build from source

Needs the .NET SDK. The game's own assemblies are referenced, not included: copy them from
`OxygenNotIncluded_Data/Managed` into a `lib/` folder beside this README, or point `ONI_LIB` at
that `Managed` folder. Then:

```
dotnet build OniPerf/OniPerf.csproj -c Release
```

## Credits

Oxygen Not Included is developed and published by Klei Entertainment. This mod is not affiliated
with or endorsed by Klei. Every lever patches the game at run time through Harmony. The exception
is the optional `fastLoad`: three of its files follow the game's own load code closely, are not
under the MIT licence, and are to be replaced by independently written code (`NOTICE`).
How this project follows Klei's modding guidelines, rule by rule: [COMPLIANCE](https://github.com/Salacious-Oni-Dev/oni-sdk-docs/blob/main/COMPLIANCE.md).

- **Fast Track and Fast Save** by Peter Han ([peterhaneve/ONIMods](https://github.com/peterhaneve/ONIMods),
  MIT) were read as the reference for what a performance mod in this game can do and where the
  costs are. No code from either is used; `cpuFix` exists so that Fast Track runs on the current
  build.
- **`brainRate`** is ported from the author's own earlier brain-rate mod, with one change: it
  spreads the allowance across frames instead of skipping whole frames.
- The other 19 levers came from measuring this game's frame. The Steam Workshop's performance
  mods (Lag Fix, CycleTrim, Performance Assist, Clean Memory & Performance Capture, Lightning fast
  Menus and others) were surveyed before this work started; none of their code is used, and no
  lever here reimplements one of them.

- **Test colonies:** 'Some light torture' and 'Euphoria v22.3': saves provided to the project; author unknown, contact us for credit.
  Large player colonies used during development, alongside the scripted test colony above. Neither
  save is shipped with the mod.

Development of this project uses AI coding assistants. All changes are reviewed, tested and
released by the maintainer.

## Licence

MIT (`LICENSE`), except the three `fastLoad` files named in `NOTICE`.
