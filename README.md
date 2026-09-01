# MacroShelf — SolidWorks macro library toolbar

MacroShelf is a SolidWorks add-in that turns a folder of macros on your PC into a
CommandManager tab. Point it at your macro library once, and every macro shows up
as a toolbar button with its own icon — no more digging through Tools > Macro > Run.

**Tested on SolidWorks 2022, 2024 and 2025.** Other versions are likely to
work; they are simply untested.

> **Need macros to fill it with?** The [MacroShelf
> Collection](https://github.com/james-debono/macroshelf-collection-sw-macro-library) is ten
> ready-to-use macros in one download, already structured as a MacroShelf
> library — unzip it, point MacroShelf at the folder, and the toolbar fills
> itself in. It is also the quickest way to see what this add-in does.

## Install (double-click the MSI)

The `.msi` from the [latest release](../../releases/latest) is the only file you
need.

1. Close SolidWorks.
2. Double-click the MSI, click Install, and accept the administrator prompt.
   - The installer is unsigned, so Windows SmartScreen may warn about an
     unknown publisher — click **More info > Run anyway**.
3. Start SolidWorks and open any document. The **MacroShelf** tab appears in the
   CommandManager. If you don't see it:
   - Tick **MacroShelf** under **Tools > Add-Ins** (both columns), and/or
   - Right-click any CommandManager tab name and tick **MacroShelf** in the list.

## First use

1. Click the **MacroShelf** tab.
2. Click **Library > Setup** to open the Library Manager, click
   **Add Library…** and select the folder that contains your macro library.
3. The toolbar populates when you click OK. Whenever you add or remove macros,
   click **Library > Scan** to refresh the toolbar.

The Library Manager also handles:

- **Multiple libraries** (up to 10) — e.g. a shared library on a network drive
  plus a personal or project one; all merge into the one toolbar. Each library row has
  its own Scan button, and unticking a library hides everything in it (its
  buttons disappear from the list and the toolbar, and come back untouched when
  you tick it on again).
- **Hiding buttons and individual macros** — untick a button to keep it off
  your toolbar, or expand a drop-down to untick single macros inside it.
  Leave one macro enabled and the button becomes a normal one-click button;
  turn them all off and the button disappears. Choices persist across scans.
- **Custom ordering** — drag buttons to rearrange the toolbar. Until your
  first drag, ordering is alphabetical and new macros slot in alphabetically;
  after it, the arrangement is locked and new macros append at the end.
  **Sort A–Z** returns to automatic alphabetical ordering.
- **"Not shown"** — any folder holding macros that doesn't follow the layout
  rules, with the reason.

**Library > Guide** opens a reference window with the folder-structure rules,
icon requirements, description tips and naming tips — handy to point other
users at.

## How to organise your macro library folder

**A folder is one thing** — it holds its macro, its `icon` and its
`description`, and the folder's name is what appears on the toolbar. A drop-down
is the same idea one level up: a folder holding folders instead of a macro.

```
My Macro Library\               <- the folder you pick in Library > Setup
│
├── Save As DXF\                <- a macro folder = a normal button
│   ├── SaveAsDxf v2.1.swp          (file name is never shown)
│   ├── icon.png                    (optional)
│   └── description.md              (optional)
│
└── Sheet Metal Tools\          <- folders inside = a drop-down button
    ├── icon.png                    (optional - main button icon)
    ├── description.md              (optional - main button hover text)
    │
    ├── Flatten All\            <- one entry in the drop-down
    │   ├── flatten_0.3.swp
    │   ├── icon.png                (optional)
    │   └── description.md          (optional)
    │
    └── Export Flat\            <- another entry
        └── export.swp
```

Rules:

- **Folder name = the name shown**, for buttons and drop-down entries alike.
  Macro file names are never displayed, so version numbers in file names are
  fine — and swapping in a newer macro file keeps all your settings.
  Names wrap onto multiple lines at spaces, so "Export Flat Pattern" shows as
  tidy stacked lines while "ExportFlatPattern" stays on one long line.
- **A folder with one macro file** → a button that runs it.
- **A folder with macro folders inside** → a drop-down, one entry per folder.
- **Only these two levels are examined**; anything deeper is ignored.
- **Icons:** any image in a folder becomes that folder's icon (button or
  drop-down entry). Name it `icon.png`/`icon.bmp` if the folder has more than
  one image. Square, 128×128 or larger looks best; for BMPs the top-left corner
  pixel colour is treated as transparent, while PNGs use real transparency.
  Entries without their own image use the main button's icon, and a folder with
  no image at all gets a generated letter tile.
- **Descriptions:** must be named exactly `description.md` (or `.txt`), so a
  readme or licence file is never picked up by mistake. On a button the text
  appears as a tooltip; on a drop-down entry it appears in the status bar at the
  bottom of the SolidWorks window (SolidWorks doesn't show hover boxes there).
  Write one or two plain sentences: start with a verb, say what it acts on and
  produces, and note anything it needs first — e.g. "Exports the active drawing
  as a PDF to your Desktop. Requires a drawing to be open."
- Both `.swp` and legacy `.swb` macros are supported.
- **Anything that doesn't follow the rules isn't shown**, and is listed with the
  reason under "Not shown" at the bottom of the Library Manager — several loose
  macros in one folder, a macro file mixed with macro folders, or macros left in
  the library root. Folders containing no macros at all are ignored silently.

Button size matches the standard large SolidWorks buttons (same as **Convert
Entities** on the Sketch tab).

## Version numbers

The Library Manager shows a **Version** column beside each macro. It is read from
inside the macro file every time you open the window, so it always matches the
macro that actually runs — it cannot drift out of step the way a note kept beside
the macro could.

To give your own macro a version, put a comment line of its own near the top of
its code:

```vba
'   Version   1.0.0
```

- **Two to four numbers separated by dots** — `1.0.0` is the usual form, `1.0` is
  fine if that is how your macro is numbered, and a fourth part like `1.0.0.5` is
  handy for telling apart copies you hand round while testing. A bare `1` is not
  enough.
- It must be a **comment line**, and the version must be the last thing on it.
  `' Version 1.0.0 (beta)` is not read: once anything follows the number, the
  line is no longer simply a version.
- Capitalisation, indenting and spacing make no difference, and a colon is
  accepted — `' version: 1.0.0` works just as well.

The version comes from the **saved macro file**, so edit the macro in the
SolidWorks VBA editor, save it there, then reopen the Library Manager to see the
change.

A blank simply means the macro carries no such line. That is normal for macros
written by other people, and nothing is wrong.

## Checking for updates

**Library > Check for updates** asks GitHub whether a newer MacroShelf has been
released, and tells you what it finds. If there is one, it offers to open the
releases page in your browser — you download and install the MSI yourself, the
same way you installed this one.

**It only ever checks when you click it.** MacroShelf does not contact GitHub when
SolidWorks starts, does not check on a timer, and sends nothing about you or your
macros — the request is the same one your browser would make opening the releases
page. Nothing is downloaded and nothing is installed automatically.

Once a check has found an update, **Update available — x.y.z** appears in the
Library menu above *Check for updates* for a few minutes, so you can get back to
it without checking again.

If your machine cannot reach GitHub — no connection, or a corporate proxy in the
way — it says so and points you at the releases page. Nothing else is affected.

## If you use the classic toolbars

Most people use the **MacroShelf tab** on the CommandManager, which always shows
your current macros and needs nothing explained. But MacroShelf also provides a
classic floating toolbar, under **Tools > Customize > Toolbars**, and that one has
a quirk worth knowing about before it surprises you.

**Tick it on just after starting SolidWorks**, before changing your library. It
will then still be there next time you start.

**Changing your library greys it out until you restart.** Add or remove a library,
switch a macro on or off, or run a scan, and the toolbar's buttons stop working —
they grey out and can't be clicked. Restart SolidWorks and the toolbar comes back
working, with your new set of macros.

This is a SolidWorks limitation rather than something MacroShelf can fix. A toolbar
is tied to the set of commands that existed when it was created, and changing your
library necessarily replaces them.

**You'll also see extra entries appear**, named like `MacroShelf (temp2 - do not
use)`. Ignore them — they work for the rest of the session but are discarded when
SolidWorks closes, taking your choice with them. Only the plain **`MacroShelf`**
entry survives a restart. They're labelled precisely so you don't tick one and lose
it later.

**The MacroShelf tab is not affected by any of this** — it updates immediately and
always matches your Library Manager. Nothing is ever unreachable; if the toolbar is
greyed out, your macros are still on the tab.

## Known issues

Stated plainly rather than left to be discovered. All of these are in the classic
toolbars; **the MacroShelf tab has none of them.**

### A dragged toolbar loses its position when you change your library

Drag the MacroShelf toolbar somewhere you prefer and it stays there across
restarts — **as long as you don't change your library.** Switch a macro on or off
in the Library Manager and the next start puts it back at the default position,
sometimes leaving one dead greyed-out button behind.

0.7.2 fixed the first half of this: before it, a dragged toolbar returned to the
default on *every* restart, whatever you did. The rebuild case is not fixed.

Four attempts have gone into it and each looked right before being measured, so
the honest position is that the mechanism is not yet understood rather than that a
fix is imminent. The evidence is recorded in `docs/DEVELOPMENT.md` §7.3b, including
what has been ruled out — worth reading before trying a fifth.

**Workaround:** get your library the way you want it, then drag the toolbar and
leave it. Position survives indefinitely while the macro set is unchanged.

### Extra rows appear in Tools > Customize > Toolbars

Every rebuild of the UI adds a row to that list, and nothing removes them. They
collapse back to one when SolidWorks restarts.

This is a SolidWorks limitation, not something an add-in can work around: a
command group cannot have its items changed, so a rebuild must create a new group,
and removing the old one does not remove its row. `RemoveCommandGroup2` reports
success without releasing anything — `NumberOfGroups` climbs and never comes back
down. Four approaches were measured; one of them crashed SolidWorks twice.
Recorded in `docs/DEVELOPMENT.md` §7.3a and §7.3b. **Cosmetic**, and it only grows
when you deliberately change something.

### The installer is unsigned

SmartScreen warns on every machine. Choose **More info > Run anyway**. Signing
costs money and is not worth it for a free tool at 0.x.

## Uninstall

**Settings > Apps > Installed apps > MacroShelf > Uninstall** (or re-run the MSI
and choose Remove).

## Building from source

**No Visual Studio or .NET SDK needed** — the build uses the C# compiler that
ships with Windows:

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
```

That produces the DLL and packages the MSI into `releases\`. On the first run it
downloads the portable WiX 3.14 toolset into `tools\wix\` (about 116 MB, pinned to
an exact release so builds stay reproducible) — after that it's cached.

You need SolidWorks installed: the build references the 2022 interop DLLs from
`api\redist`, deliberately the oldest version, so one DLL loads in 2022, 2024 and
2025.

There's an offline test suite that needs no SolidWorks:

```powershell
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1
```

**Versioning:** the single source of truth is `AssemblyVersion` in
`src\AssemblyInfo.cs` — the MSI filename, product version and COM registration all
follow it. A rebuilt MSI with the same or higher version upgrades an existing
install in place.

Technical detail — the library format rules, the SolidWorks API findings that cost
real debugging, and the traps — is in
[docs/DEVELOPMENT.md](docs/DEVELOPMENT.md).

## Troubleshooting

- **Tab missing after install** — open a document first (CommandManager tabs only
  show when a document is open). Then check Tools > Add-Ins.
- **A macro fails to run** — MacroShelf looks for a parameterless entry point
  (preferring `Sub main`). Make sure the macro runs from Tools > Macro > Run.
- **Toolbar didn't update after editing the library** — click **Library > Scan**.
- **Buttons show but icons look wrong** — check the folder's BMP; remember the
  top-left pixel colour becomes transparent.
- **Your settings** — libraries, button order and on/off states — are in
  `%AppData%\MacroShelf\settings.json`. Generated icons are cached alongside it and
  are safe to delete; they are rebuilt on the next start.
- **If something fails**, MacroShelf writes details (including stack traces) to
  `%AppData%\MacroShelf\macroshelf.log` — include it when reporting a problem.

## Licence

MIT — see [LICENSE](LICENSE). Free to use, modify and share.

The installer places a single file, `MacroShelf.dll`, and nothing else. MacroShelf
uses the **SOLIDWORKS API assemblies** belonging to Dassault Systèmes but does not
contain or include them — it loads them when it runs, from the SOLIDWORKS
installation you already have. See
[THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).

Created by James Debono, with AI assistance. Everything here was tested by
hand in SOLIDWORKS — nothing that touches the API can be verified any other way.

## Trademarks

SOLIDWORKS is a registered trademark of Dassault Systèmes SolidWorks Corporation.
This project is independent: it is not affiliated with, endorsed by, or sponsored
by Dassault Systèmes, and uses only the published SOLIDWORKS API.
