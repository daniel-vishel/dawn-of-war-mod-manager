# Dawn of War (Anniversary Edition) — Mod Manager

*[Русская версия](README.ru.md)*

A set of mods for **Warhammer 40,000: Dawn of War — Anniversary Edition** (Steam)
with a graphical manager: widescreen rendering, an unstretched interface,
extended camera zoom, fog distance, and Russian localisation.

| Mod | What it does | Rollback |
|---|---|---|
| [widescreen](widescreen/) | Genuinely widens the field of view, rather than stretching it | from the `_widescreen_backup` copy |
| [ui-unstretch](ui-unstretch/) | Unstretched interface: 4:3 panel proportions, moved into the screen corners | by manifest |
| [camera-zoom](camera-zoom/) | Extended camera zoom-out on the mouse wheel | delete a single file |
| [fog-distance](fog-distance/) | Fog stops eating objects when the camera is pulled far back | by manifest |
| language | Localisation pack + `[lang:russian]` in `W40k.ini` | restore `W40k.ini` from the copy |

Tested at **3440x1440 (21:9)**; any resolution is supported.

> **After Apply the game launches the ordinary way from Steam.** The manager is
> only needed to configure things, not to play. Game files are patched on disk,
> but with a full backup: rollback is one click.

> ⚠️ **Single-player only.** A changed aspect ratio in multiplayer is an
> information advantage and a desync risk.

Requirements: Windows, PowerShell 5.1+ (already in the system), the game
installed through Steam.

---

## 0. The manager — everything in one window

**`DoW-ModManager.exe`** is a graphical application (double-click it). Only the
source is kept in the repository: build the `.exe` once with the .NET
Framework 4.x compiler that ships with Windows — no SDK required.

```powershell
powershell -ExecutionPolicy Bypass -File .\app\Build-App.ps1
```

`DoW-ModManager.exe` appears in the repository root next to the scripts. The
app does not duplicate any mod logic: it collects settings, calls
`DoW-Launcher.ps1` and streams its log live.

### What it can do

- **Game folder** — auto-detected (usual Steam paths plus the registry) or
  picked by hand.
- **Detects what is already installed.** On start it reads the real state off
  disk (whether the DLLs are patched, whether the UI mod and the zoom are in
  place, which locales are present, what `W40k.ini` and `Local.ini` say) and
  ticks the boxes accordingly. Anything installed earlier or by hand shows up.
- **Apply** is enabled only when the window differs from what is actually
  installed.
- **Defaults** returns the window to the recommended values; it does not touch
  the game until Apply is pressed.
- **Rollback** restores the game files and clears every box.
- **Launch game** is always available and starts the game as-is, applying
  nothing.
- The **log** collapses with a button; the **theme** follows the system.
- Every option is documented on the **?** dots next to it — hover to read.

### Icon and header

The app picks up `app\app.ico` (window and `.exe` icon) and `app\header.png`
(the header strip, 688 × 84). Both are in the repository and the icon is
embedded into the `.exe` at build time. Without them the app still works and
simply shows a text header.

### Headless (console)

```powershell
.\DoW-Launcher.ps1 -Status       # current game state as JSON
.\DoW-Launcher.ps1 -LocaleInfo   # which locales are really installed
.\DoW-Launcher.ps1 -Apply        # apply the settings from launcher-settings.json
.\DoW-Launcher.ps1 -Launch       # apply the settings and start the game
.\DoW-Launcher.ps1 -RestoreAll   # full rollback
```

---

## 1. Widescreen (a wider field of view)

The engine stores the aspect ratio as the constant `1.3333` (4:3, hex
`AB AA AA 3F`) in `Platform.dll`, `spDx9.dll` and `UserInterface.dll`.
Replacing it with the real screen ratio (`3440/1440 = 2.3889`) makes the engine
render **a wider slice of the world**: proportions stay correct and unit
selection circles stay circles.

The patch is written **to disk** (`widescreen\DoW-Widescreen-Patcher.ps1`),
which is exactly why the game works afterwards when started normally from
Steam. Before the first edit the originals are copied into
`<game>\_widescreen_backup`, and rollback restores them from there. The
resolution is written into `Local.ini`, which is then marked read-only so the
game cannot reset it from its own settings menu.

### Minimap (a known trade-off)

The same constant in the exe also drives the minimap. The modes (labelled
"Мини-карта (exe)" in the window):

| Mode | exe | Effect |
|---|---|---|
| `skip` (recommended) | untouched | the world is correct; the UI mod fixes minimap squareness through the layout |
| `compromise` | 1.25 | helps the minimap on some builds; as a side effect it distorts the main menu, though in battle everything is fine |
| `full` | real aspect | the minimap is fine for some users, but the world may stretch |

Order to try: `skip` (with the UI mod) → if the minimap is still broken →
`compromise`.

### Important

**Do not change the resolution in the game settings** — the game will rewrite
`Local.ini`. Set the resolution only from the manager.

---

## 2. Unstretched UI

The interface layout lives in `Engine\Engine.sga` →
`data/art/ui/screens/*.screen` (Lua tables; the position and size of every
widget are given in screen fractions). On a wide screen the engine stretches
that layout across the full width: portraits turn oval, and the minimap and the
panels are distorted.

`ui-unstretch\Install-UnstretchedUI.ps1`:

1. Extracts the layout from **your own** `Engine.sga`, so it works with any
   version of the game.
2. Squeezes the HUD panels horizontally by `k = (4/3) / (width/height)` — the
   proportions of 4:3, with the minimap no longer distorted — and **spreads
   them into the corners**: the minimap and the resources to the left edge; the
   squad selection panel, the command buttons and the menu buttons as one block
   to the right. The centre of the screen stays clear, showing the 3D world.
3. The bar backgrounds are drawn as a single full-width texture, so the
   installer **cuts them into slices** at the functional zone boundaries (from
   your own `Engine.sga`, for every race). Each slice travels together with its
   own buttons, so the buttons stay exactly on their painted sockets.
4. Buttons with no explicit size in the layout are given the size of a sibling
   with the same style — otherwise they do not scale and overlap their
   neighbours.
5. The 3D world (`ctmSimVis`) is expanded to the whole screen.
6. The result is installed as loose files into `Engine\Data\` on top of the
   archive; the original archives are never modified.

### Bar textures

- **Hard cut** — the stock textures, cut at the boundary with the map. The
  parts move apart into the corners, so at the seams you see where a once
  continuous panel was cut. **This is currently the only mode.**
- **Repainted** — *work in progress, locked in the manager.* The idea is a
  panel that ends on a soft, tidy edge instead of a hard cut.

No repainted artwork is shipped here and none will be: such art is derived from
the game textures, which is third-party content that cannot be redistributed
with the mod.

The machinery is in place, so your own art can still be wired up by hand:

```powershell
.\ui-unstretch\Edit-BarTextures.ps1 -Export    # export the slices as PNG
.\ui-unstretch\Edit-BarTextures.ps1 -Preview   # check alignment, no game needed
.\ui-unstretch\Edit-BarTextures.ps1 -Import    # push the edits into the game
```

Fully repainted slices of any size go into
`ui-unstretch\textures-custom\<race>\<slice>.png` (the folder is gitignored).
On `-Import` the background connected to the image border is keyed out, and the
art is fitted to the original slice box, so buttons and the minimap stay in
place.

**Enclosed holes in the panel** (the command card, the portrait sockets) have to
be transparent so the world and the buttons show through. The border flood fill
never reaches them — they are walled in by the frame. Two ways to cut them out:

- paint real transparency in an editor and disable the keying:
  `align.json` → `{"threshold":-1}`; or
- fill each hole with a marker colour (magenta) and name it:
  `{"chroma":"FF00FF","chromaTol":60}` — the colour is keyed out everywhere,
  enclosed regions included. Pick `chromaTol` so it does not eat the frame
  metal.

---

## 3. Camera Zoom

The camera parameters live in `Engine\Engine.sga`, in `camera_high.lua`. The
engine reads **loose files from `<game>\Engine\Data` over the archive**, so the
installer simply drops a modified `camera_high.lua` there (maximum distance
`DistMax`: 38 → 76). Original files are not modified.

**Compatibility with campaigns in progress.** Unlike the Steam Community guides,
where the mod is installed as a separate module and requires a new campaign,
here the file goes to the engine level rather than into the campaign module data
(W40k/WXP). Camera parameters are not stored in saves, so **campaigns and saves
already in progress keep working**.

**Required:** enable **Full 3D Camera** in the game graphics settings, otherwise
the simplified camera (`camera_low`) is used and the mod has no effect.

At long range the map is swallowed by fog: a map's distance fog is calibrated
for the stock `DistMax = 38`, so objects past that boundary fade into haze. 76 is
a sensible balance **without** the fog fix; with the [fog-distance](fog-distance/)
module (section 4) you can pull back further.

---

## 4. Fog Distance

Fog distance and sky radius are defined **in the maps themselves**, not in the
camera files — `camera_high.lua` cannot change them. The values are calibrated
for the stock camera distance, so with a raised `DistMax` units and buildings
past the fog boundary wash out into haze and the high view becomes useless.

The module **does not disable the fog, it pushes it back**: the atmosphere
stays, the objects remain visible.

The campaign maps (`scenarios/sp/*.sgb`) use the Relic Chunky format. Under
`FOLD SCEN > FOLD WSTC > FOLD TERR`:

| Chunk | Field | Offset |
|---|---|---|
| `DATA EFFC` | fog distance (float) | `+40` from the start of the chunk data |
| `DATA HRZN` | sky radius (float) | after `uint32 nameLen` and the sky name |

Both edits overwrite a float **in place**, so chunk sizes never change.

`fog-distance\Install-FogDistance.ps1` reads the maps from **your own** `.sga`,
patches them in memory and writes loose files into
`<game>\W40k\Data\scenarios\sp\`. The original archives are not modified — the
same trick `camera-zoom` uses.

```powershell
.\fog-distance\Install-FogDistance.ps1                          # 1000 / 512
.\fog-distance\Install-FogDistance.ps1 -FogDistance 1500 -SkyRadius 700
.\fog-distance\Install-FogDistance.ps1 -Restore                 # rollback
```

> This module is not wired into the manager window yet; run it from the console.

### About saves

A campaign save references map data, so **with the fix active older saves may
refuse to load** — the authors of similar mods warn about the same thing. Here
it is **reversible**: `-Restore` deletes the loose maps, the vanilla ones come
back from the archive, and the old saves work again. The recommendation is to
enable the fix on a fresh campaign.

---

## 5. Russian language

The manager writes `[lang:russian]` into `W40k.ini` (with a backup) and can
unpack a localisation archive into the game folder.

### Detecting what is actually installed

The manager scans `<game>\<module>\Locale\<Language>\` (W40k, WXP, DXP2, Engine)
and treats a locale as installed only when it holds content — `.ucs` and/or
`.sga` files. **An empty `Locale\Russian` folder does not count.** The locales
found are shown in the status line under the checkbox.

Console diagnostics:

```powershell
.\DoW-Launcher.ps1 -LocaleInfo
```

It reports every locale (file counts, `.ucs`, `.sga`, full paths), which of them
are usable, what `W40k.ini` says, and which extractor is available for archives.

### Crash guard

The engine dies at startup when `[lang:X]` points at a locale that does not
exist. That is exactly what happened when Russian was switched off: the
localisation had replaced the English locale, the manager wrote
`[lang:english]`, and the game crashed.

The language is now switched **only to a locale that really exists**. If the
English locale is missing, the manager refuses to switch, leaves `W40k.ini`
untouched and says so. To get English back, verify the integrity of the game
files in Steam (Properties → Installed Files → Verify integrity).

### Installing

The **"Скачать русификатор…"** button opens the
[Steam guide](https://steamcommunity.com/sharedfiles/filedetails/?id=3421728842)
with the mirrors (Playground / Google Drive / Yandex.Disk / Mail.ru Cloud). Point
the manager at the downloaded archive with **"Указать архив…"**.

**zip, rar, 7z** and other formats are supported: the extractor is resolved from
the registry, `PATH` and the usual install folders (7-Zip, then WinRAR); `zip`
works without either. A redundant wrapper folder inside the archive is stripped
automatically, so the files land in the right places.

**The localisation files are not in this repository and never will be:** they are
third-party content — a translation and voice-over of a commercial game,
published without a redistribution licence.

---

## 6. Reference: aspect ratio hex values

| Ratio | Resolution | Float | Hex (LE) |
|---|---|---|---|
| 4:3 (stock) | 1600x1200 | 1.3333 | `AB AA AA 3F` |
| 16:10 | 1920x1200 | 1.6000 | `CC CC CD 3F` |
| 16:9 | 1920x1080 | 1.7778 | `39 8E E3 3F` |
| 21:9 | 2560x1080 | 2.3704 | `26 B4 17 40` |
| **21:9** | **3440x1440** | **2.3889** | **`8E E3 18 40`** |
| 32:9 | 5120x1440 | 3.5556 | `39 8E 63 40` |

## 7. Sources

- [Ultra widescreen Fix 21:9 (Steam)](https://steamcommunity.com/sharedfiles/filedetails/?id=1132691115) — hex values, "do not touch the exe"
- [A better 16:9 widescreen fix (Steam)](https://steamcommunity.com/sharedfiles/filedetails/?id=2552610755) — the 1.25 compromise for the minimap
- [Dawn of War Widescreen Fix (Steam)](https://steamcommunity.com/sharedfiles/filedetails/?id=205040803) — the basic method
- [Localisation (Steam guide)](https://steamcommunity.com/sharedfiles/filedetails/?id=3421728842) — links to the localisation archive
- [Relic RDN Wiki: Capturing Screenshots](https://dow.finaldeath.co.uk/rdnwiki/www.relic.com/rdn/wiki/CapturingScreenshots&v=x7.html) — the stock `camera_high.lua`
- [Zoom Out Further (AE discussion)](https://steamcommunity.com/app/4570/discussions/0/3183484418860439190/) — the `Engine\Data` path
- [zero334/Dawn-of-War-Widescreen-Fix](https://github.com/zero334/Dawn-of-War-Widescreen-Fix) — automation reference
- [ModernMAK/Relic-Game-Tool](https://github.com/ModernMAK/Relic-Game-Tool) — SGA v2 format reference
- [Unstretched UI (Soulstorm/DC)](https://www.moddb.com/mods/unstretched-ui) — the unstretched UI idea (not compatible with AE)
- [Increased Fog Distance & Sky Radius](https://www.nexusmods.com/warhammer40000dawnofwar/mods/2) — reference values for fog and sky (1000 / 512); the chunk format was reverse-engineered here
- [Changing the camera range (Steam guide)](https://steamcommunity.com/sharedfiles/filedetails/?id=3413196417) — where the fog problem was posed, and the warning about saves
