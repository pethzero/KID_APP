# KID Helper (KID_APP)

A tool for writing `_KID.ini` lines for [Keyword Item Distributor](https://www.nexusmods.com/skyrimspecialedition/mods/55728)
without converting FormIDs by hand.

```
Keyword = formID~esp (OR) keywordEditorID | type | strings,formIDs (OR) editorIDs | traits | chance
```

---

## How to use

| Step | Panel | What to do |
|---|---|---|
| ① | **KEYWORDS** | Pick a keyword file (e.g. `Smooth Weapon.esm`), then tick the keywords you want to add |
| ② | **SETTINGS** | Form Type (only `Weapon` for now), Traits, Chance, and output options |
| ③ | **ITEMS** | **📂 จาก ESP (from ESP)**: open or drag in an `.esp/.esm/.esl` file, then tick weapons from the list<br>**✎ พิมพ์เอง (type it yourself)**: type `plugin.esp FormID` / `EditorID` one per line |
| ④ | **OUTPUT** | Copy / Save as / Append to an existing `_KID.ini` |

### ③ From ESP

You open the plugin directly (e.g. `[Super Hero Pack].esp`), and the program lists every **Weapon** in that file:

| Shown | Meaning |
|---|---|
| `04000914` | xEdit-style FormID. The first two digits come from the **Load order** box (type the number xEdit shows, e.g. `04`). Display only; it doesn't change the output |
| EditorID / name | from `EDID` / `FULL` |
| `Greatsword`, `Bow` … | the weapon's animation type (`DNAM`) |
| `DMG 25` | base damage |
| keyword list | keywords already on the weapon (`KWDA`). Names are read from the plugin itself and from master files found in the same folder or the game's `Data` folder |
| `override` | a record that edits a master's weapon (e.g. a vanilla weapon). The output points to the master, e.g. `0x12EB7` (Skyrim.esm) |

ESL / ESL-flagged plugins are detected from the header flag automatically (the last 3 digits are used).

### FormID conversion rules

| Plugin | xEdit FormID | KID |
|---|---|---|
| Skyrim.esm | `0001397E` | `0x1397E` |
| ESP / ESM | `04000914` | `0x914~[Super Hero Pack].esp` (keep the last 6 digits, drop leading zeros) |
| ESL / ESL-flagged | `FE044801` | `0x801~ccfsvsse001-backpacks.esl` (keep the last 3 digits) |

---

## Keyword file format (`keywords\*.txt`)

The program reads every `.txt` file in the **`keywords`** folder next to `KIDHelper.exe`.
**1 file = 1 keyword set** (the plugin that contains the KYWD records). Use the 📁 button to open the folder and ↻ to reload.

### Minimal example: paste the header from an existing ini directly

```
Smooth Weapon.esm
;0x800	Weap TypeRapier
;0x801	Weap TypePike
;0x803	Weap TypeHalberd
```

### Full example

```
# a line starting with # is a comment
plugin = Smooth Weapon.esm        # plugin that contains the keywords (required for 0x IDs)
name   = Smooth Weapon (Pethzero) # name shown in the dropdown (optional, defaults to plugin)
type   = Weapon                   # default Form Type (optional, defaults to Weapon)

0x800	Weap TypeRapier
0x801 - Weap TypePike
;0x803	Weap TypeHalberd
WeapTypeSword	Vanilla sword keyword
```

### Rules

| Line | Meaning |
|---|---|
| `# ...` or `// ...` | comment, ignored |
| `plugin = X.esm` | keyword plugin. Writing just `X.esm` on its own line also works |
| `name = ...` | display name in the dropdown |
| `type = Weapon` | default Form Type |
| `0x800<TAB>Name` | keyword by FormID (already shortened to the local ID, like KID uses) + display name |
| `0x800 - Name` | same as above (dash separator) |
| `EditorID` / `EditorID<TAB>Name` | keyword by EditorID, e.g. `WeapTypeSword` |
| a leading `;` | allowed, so you can paste the comment header from a `_KID.ini` directly |
| `;-----` | separator line, ignored |
| free text with spaces (`; Smooth Weapon Edit`) | ignored (not a keyword) |

The output is built like this:
- `0x800` + `plugin = Smooth Weapon.esm` → `Keyword = 0x800~Smooth Weapon.esm|Weapon|...`
- `0x800` + `plugin = Skyrim.esm` → `Keyword = 0x800|Weapon|...` (base game needs no `~`)
- `WeapTypeSword` → `Keyword = WeapTypeSword|Weapon|...`

### Ways to add a new keyword file

1. Create a `.txt` in the `keywords\` folder by hand, then press ↻
2. Drag a `.txt` file onto the program window (it is copied into `keywords\` automatically)
3. Press **เปิด _KID.ini… (Open _KID.ini)**, pick an existing ini (it reads the `;0x800 Name` header), then **✎ แก้ list (edit list) → 💾 บันทึก .txt (save .txt)**
4. Press **✎ แก้ list (edit list)**, paste the list, then **💾 บันทึก .txt (save .txt)** (the file is named after the Keyword plugin)

The files are UTF-8 (Thai is fine), with Windows (CRLF) or Unix (LF) line endings.

---

## Folder layout

```
KID_APP\
├─ KIDHelper.exe      ← the program (single file, runs without installing .NET)
├─ keywords\          ← keyword files (*.txt), edit / add freely
├─ README.md
├─ build.bat          ← rebuild the exe from src\
└─ src\               ← source code (C# / WPF, .NET 8)
```

If you move `KIDHelper.exe` somewhere else by itself, it creates a `keywords\` folder with the default sets automatically.

## Build

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) (only for building; running doesn't need it)

```
build.bat      →  KIDHelper.exe   (build never overwrites keywords\)
```

## Roadmap
- [x] Read Weapon from ESP/ESM/ESL
- [ ] Armor, Ammo and the other Form Types (currently locked to Weapon)
- [ ] Automatic load order from `plugins.txt` / MO2 profile
