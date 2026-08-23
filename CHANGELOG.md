# Changelog

Semantic Versioning; `MAJOR` reaches 1 when the behaviour is settled enough to
promise not to break it. The version
lives in `AssemblyVersion` in `src\AssemblyInfo.cs` and everything else — the MSI
filename, ProductVersion and COM registration — derives from it.

Dates are taken from the built installers.

---

## 0.8.0 — unreleased

- **Renamed from MacroDeck to MacroShelf.** The previous name belongs to an
  existing product — Macro Deck by SuchByte, in the Stream Deck space — so it
  could not go public as it stood. MacroShelf fits the vocabulary the add-in
  already used: it manages a *library*, and the Library button has always been
  three book spines on a shelf.

  The add-in, the installer, the DLL and the settings folder all take the new
  name. **The add-in's CLSID and the installer's UpgradeCode are unchanged**, so
  this installs over an earlier version as a normal upgrade rather than sitting
  alongside it.

- **Your settings come across automatically.** Preferences moved from
  `%AppData%\MacroDeck\` to `%AppData%\MacroShelf\`. On first run the old
  `settings.json` is copied over and the old folder is then removed, so your
  library list and per-macro toggles survive the rename without you doing
  anything. The old folder is deleted only after the copy has been read back and
  parsed — if anything goes wrong the original is left alone and it tries again
  next time.

  If you later reinstall 0.6.2 or earlier, it will start with an empty Library
  Manager, because the folder it reads no longer exists.

---

## 0.7.2 — 2026-08-22

- **First public release.** MacroShelf's source is now on GitHub under the MIT
  licence, and the update check points at it.

- **Fixed: Check for updates never found anything.** The endpoints were hard-coded
  to a repository name that changed before publication, so the check asked about a
  repository that does not exist. Any earlier build has the same fault and cannot
  be fixed in place — install 0.7.2 to get working update checks.

- **Added: a Known issues section in the README**, covering the classic-toolbar
  behaviour below and the extra Customize rows, so neither is discovered by
  surprise.

- **Partly fixed: a repositioned classic toolbar.** Drag the MacroShelf toolbar
  somewhere else and it stayed there for the session, then reappeared at the
  default position next time SOLIDWORKS started. It now holds across restarts —
  **as long as the library is not changed.**

  Removing a command group takes a flag saying whether to remove only the runtime
  registration or the toolbar's registry entry as well. MacroShelf removed the
  registry entry — and that entry is where SOLIDWORKS records where you docked the
  toolbar. It was set that way in 0.7.0 while trying to stop the duplicate
  **Tools > Customize > Toolbars** rows, which it did not do. So it was costing a
  real behaviour and buying nothing.

  That alone was not enough. `CreateCommandGroup2` also takes a flag telling
  SOLIDWORKS to discard everything it saved about the command group — including
  the docked position — and MacroShelf always passed it. It now asks for that only
  when the set of enabled macros has actually changed, so an unchanged library
  leaves your toolbar exactly where you put it.

  **Still broken, and known:** switching a macro on or off leaves the toolbar at
  the default position on the next start rather than where you put it, and can
  leave one dead greyed-out button behind. Changing your library therefore still
  costs you the toolbar's position. Tracked; not fixed in this version.

## 0.7.1 — 2026-08-15

- **The installer installs a single file, `MacroShelf.dll`.** The SOLIDWORKS API
  interop assemblies are loaded when MacroShelf runs, from the SOLIDWORKS
  installation already on the machine — the same one the add-in exists to talk to.

  This is also the only approach that works across releases: the assemblies are
  strong-named and their version moves with each SOLIDWORKS release, so a fixed
  reference to one version cannot load another. One build still serves SOLIDWORKS
  2022, 2024 and 2025.

## 0.7.0 — 2026-08-15

- **Hand-drawn icons for the Library menu.** Setup, Scan, Guide, Check for updates
  and Update available now use proper artwork rather than the shapes the add-in
  drew for itself. They live in `src\assets\` and are embedded into the DLL at
  build time; the drawn versions remain as a fallback, so a missing file degrades
  to a usable icon instead of breaking the toolbar.

- **Check for updates**, in the Library menu. Asks GitHub whether a newer MacroShelf
  has been released and offers to open the releases page. Nothing is downloaded and
  nothing is installed — the most it does is hand a URL to your browser.

  **Manual only, by design.** It checks when you click it and at no other time: no
  startup traffic, no timer, no first-run prompt to dismiss and no setting to find.
  That one decision is also what makes it easy for an IT department to live with,
  since the request is the one your browser would make opening the page yourself.

  Once a check finds something, **Update available — x.y.z** appears in the same
  menu for a few minutes. It lives in the menu rather than on the toolbar because a
  button that appears after an answer arrives would force a UI rebuild, and every
  rebuild leaves another stale toolbar registration behind for the session.

  Answers are held for five minutes so repeated clicks cost nothing, but failures
  are not cached — reconnect and click again and it really tries again. A release
  tagged with something that is not a version number is reported as unknown rather
  than as "up to date", which would be a false reassurance.

- **The Library Manager now shows each macro's version.** A new column down the
  right of the button list, read out of every `.swp` when the window opens. A macro
  that declares no version simply shows nothing, and a library where none of them do
  gets no column heading either.

  The number is read from **inside the macro**, not from a sidecar file or a
  description. Anything kept beside the macro can be made to disagree with it by
  swapping the `.swp`, and a version number that lies is worse than no version
  number at all.

  Getting that right meant reading the `.swp` as the OLE compound file it is,
  through `StgOpenStorage`. Scanning the file for the version string does not work:
  its free sectors keep earlier copies of the source, so a scan finds old and new
  together with no way to tell which one actually runs — measured at `0.11.1`
  alongside `0.11.0` for Apply Unique Colours. Reading a real stream stops at that
  stream's declared length, so the leftovers are never in reach.

  Versions are read **only when the Library Manager opens**, never during the
  startup scan or a toolbar build, and nothing is cached. A whole library costs a
  few milliseconds, so every reading is fresh.

  Any macro can show a version by carrying a comment line such as
  `'   Version   1.0.0`. The format is documented in the README and the Guide
  window, and the match ignores capitalisation, indenting and an optional colon,
  and takes anything from `1.0` to `1.0.0.5` — a fourth part is useful for telling
  apart copies handed round while testing — so it is hard to get wrong. A macro
  without such a line shows nothing, which is the normal case for macros written
  by somebody else.

- **Drop-down macros now appear on the classic toolbar.** A flyout cannot be placed
  on a classic toolbar — the API offers only a CommandManager tab or a context menu
  — so anyone working from the small toolbars never saw the macros inside a
  drop-down at all.

  Every enabled macro now gets its own toolbar button, in display order, so a
  drop-down's contents sit inline where the drop-down appears on the tab. The
  CommandManager tab is unchanged: drop-downs still drop down there.

  A folder's sole enabled macro keeps the folder's name and icon; one of several
  carries its own, because on the toolbar it is a button in its own right.

- **Fixed: a drop-down reduced to one enabled macro vanished from the tab.** The
  tab keyed off `IsMulti`, which counts every macro in a folder, but what belongs
  on the tab depends on how many are switched *on*. A folder of three with two
  disabled is a plain button — yet `IsMulti` stayed true, so it was absent from the
  flyout lookup and the plain-button branch never ran.

- `ICommandManager::RemoveCommandGroup2` now passes `RuntimeOnly = false`, so a
  removed command group's toolbar registration goes with it rather than being left
  in the registry. That is the right choice here because toolbar layout is
  deliberately disposable — the Library Manager is the single source of truth for
  what appears — so there is no user arrangement to preserve.

  **This does not fix the duplicate rows in Tools > Customize > Toolbars**, and
  nothing an add-in can do will. `ICommandManager.NumberOfGroups` shows that
  `RemoveCommandGroup2` reports success without releasing the group: the count
  climbs as groups are created and never comes back down. `ICommandGroup` has no
  way to remove command items, so a rebuild must create a new group, and every
  created group adds a row that removal will not take away.

  Three approaches were measured and all failed — see §7.3b of the development
  notes. The rows are cosmetic, only appear on a deliberate toggle or scan, and
  clear on restart.

- Removal failures are now logged. `RemoveCommandGroup2` returns a
  `swRemoveCommandGroupErrors` code and `RemoveFlyoutGroup` returns a bool; both
  were previously discarded inside empty `catch` blocks, so a failed removal was
  invisible until someone opened the Customize dialog.

- **The Library Manager title now shows the version** — `MacroShelf - Library
  Manager (0.7.0)`. Read from `AssemblyVersion`, so it cannot drift from the build.

- **Test builds are identifiable.** The fourth version field marks a build handed
  over for testing: a release is `MacroShelf-0.7.0.msi`, a test build is
  `MacroShelf-0.7.0.2.msi`, and the Library Manager title shows the fourth field
  when it is non-zero. An MSI ProductVersion has only three fields, so without this
  every test build of a version is indistinguishable once installed.

## 0.6.2 — 2026-08-13

- **Third-party attribution.** Added `THIRD-PARTY-NOTICES.md`, a third-party
  section on the installer's licence screen, and a note in the README, so it is
  clear which components MacroShelf builds against and that the MIT licence covers
  MacroShelf's own code only.
- No functional change.

## 0.6.1 — 2026-08-09

- **Licensed under MIT.** Copyright and licence added to the assembly metadata,
  and the installer's terms screen replaced with the licence text.
- No functional change.

## 0.6.0 — 2026-08-09

- **Strict two-level library format.** A folder is one thing: it holds its macro,
  its icon and its description, and the folder's name is what appears on the
  toolbar. A folder holding folders becomes a drop-down. Anything deeper is
  ignored.
- Folders that don't follow the rules are listed in a "Not shown" panel with the
  reason, rather than vanishing silently.
- **Library and per-macro toggles.** Untick a library to hide everything in it;
  expand a drop-down to untick single macros. Choices persist across scans.
- Custom Library button artwork, embedded into the DLL at build time.
- Sidecar files named after the macro are no longer supported. Descriptions must
  be `description.md` or `description.txt`; icons are `icon.png`/`icon.bmp`, or any
  single image in the folder.

## 0.5.1 — 2026-08-01

- **Icon scaling fix.** GDI+ interpolation of straight ARGB blends the RGB of
  transparent (black) pixels into antialiased edges, visibly greying icons made of
  thin light strokes. Icons are now scaled from premultiplied-alpha copies. A
  regression test asserts a downscaled white ring keeps zero darkened edge pixels.

## 0.5.0 — 2026-07-25

- Per-macro icons inside drop-down buttons.

## 0.4.0 — 2026-07-23

- **Multi-library manager.** Up to ten library folders, merged into one toolbar —
  a shared library plus a personal or project one. Each row has its own Scan
  button and an on/off tick.
- Pre-0.4.0 installs stored a single library path in the registry; it is read once
  and migrated.

## 0.3.1 — 2026-07-22

- Hover descriptions capped at 500 characters.

## 0.3.0 — 2026-07-22

- Hover descriptions on toolbar buttons, read from a `description` file in each
  macro folder.
- Terms screen added to the installer.

## 0.2.2 — 2026-07-21

- **Seamless rescan.** Removing and re-adding the tab made SolidWorks treat it as
  new and auto-activate it, so the toolbar flicked to another tab during a scan.
  A rescan is now double-buffered: new commands and tab boxes are created before
  the old ones are removed, so the tab is never momentarily empty.

## 0.2.1 — 2026-07-20

- Project restructure; Add/Remove Programs icon.

## 0.2.0 — 2026-07-20

- Guide window, rendering embedded HTML in a WebBrowser control.

## 0.1.2 — 2026-07-20

- Fixed a crash on rescan.
- Modern Vista-style folder picker via `IFileOpenDialog`, since WinForms on .NET
  Framework only offers the old tree-style dialog.
- Tolerance for messy libraries.

## 0.1.1 — 2026-07-20

- **Flyout click error.** A flyout's open-callback is invoked on every click and
  must rebuild its items right then. A no-op callback produced "An invalid argument
  was encountered".
- **Tab focus.** The tab stole focus at every startup; fixed by never removing it.

## 0.1.0 — 2026-07-20

- First build. Scans a library folder and builds a CommandManager tab from it.
