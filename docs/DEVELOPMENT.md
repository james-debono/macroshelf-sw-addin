# MacroDeck — development notes

The SOLIDWORKS API findings behind this add-in, and the traps. `README.md` covers
what it does and how to use it.

Each item below cost a debugging round of its own, which is why they are written
down rather than left to be rediscovered.

---

## Building it

`build.ps1` is the whole build — it compiles the DLL and packages the MSI.

```powershell
powershell -ExecutionPolicy Bypass -File build.ps1
powershell -ExecutionPolicy Bypass -File tests\run-tests.ps1   # ~48 offline checks
```

- **No Visual Studio, .NET SDK or MSBuild is needed.** Compilation uses the C#
  compiler shipped with Windows, `%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe`.
- That compiler is **C# 5 only** — no string interpolation, no `?.`, no `nameof`,
  no expression-bodied members, no `out var`. This is the single biggest
  constraint on the code and the reason it reads older than it is.
- **Build against the oldest SOLIDWORKS version you intend to support.** A build
  compiled against the 2022 interops loads in 2022, 2024 and 2025; the reverse
  does not hold.
- The MSI is built with portable **WiX 3.14** binaries that `build.ps1` downloads
  and caches, pinned to an exact release URL so builds stay reproducible.
- `light.exe` runs with `-sice:ICE38 -sice:ICE57 -sice:ICE64` because the package
  deliberately writes an HKCU key from a per-machine package. The remaining
  ICE61/ICE69 warnings are expected.

**Everything touching the SOLIDWORKS API is untestable offline** and must be
checked by hand in SOLIDWORKS. That is where every bug in this project has come
from. The test suite covers the library-format rules, settings and icon pipeline
only.

## Why the API assemblies are resolved at run time

MacroDeck installs **one file** and finds the SOLIDWORKS API assemblies on the
machine running it. Keep it that way — the MSI's file table is a one-line check
(`SELECT FileName FROM File`), and anything else appearing in it is a regression.

**A copy sitting alongside the add-in would not work.** The assemblies are
strong-named and their version moves with each release:

| | 2022 | 2024 | 2025 |
|---|---|---|---|
| `sldworks` / `swconst` / `swpublished` | 30.5.0.49 | 32.5.0.48 | 33.5.0.53 |

Ordinary strong-name binding is an exact-version match, they are not in the GAC,
and there is no publisher policy — so a fixed reference to one version cannot load
another. `AppDomain.AssemblyResolve` is the documented escape hatch: whatever the
handler returns is accepted, version differences included. That is correct here
because the COM interfaces underneath are stable.

**The blocker, and how it was removed.** The CLR loads every interface a type
implements *before* any of that type's code runs, so a class implementing the real
`ISwAddin` could not be created unless `swpublished.dll` was already reachable —
no handler of ours could be installed in time. `ISwAddin` is two methods taking
only `object` and `int`, so it is declared in `src\InteropResolver.cs` from the
IID and signatures published in the SOLIDWORKS API documentation. **Those must
match exactly**: a COM interface is called by vtable slot, so a wrong signature is
not a compile error, it is a corrupted stack at run time.

Re-declaring an interface for interoperability is ordinary COM practice, and
everything used here is documented.

Testable offline, and worth re-running after any change to add-in loading: put
`MacroDeck.dll` alone in an empty folder, install no resolver, load it, create the
type and invoke `ConnectToSW`. A `FileNotFoundException` or `TypeLoadException`
means the dependency has crept back.

## Command groups, toolbars and IDs

This is the awkward part of the API, and most of MacroDeck's scar tissue is here.

### Callbacks are strings

`AddCommandItem2` takes a **method name as a string**, resolved by reflection at
click time. A typo fails silently or at click time, never at compile time. Hence
the generated stub file.

### Command IDs are runtime and order-dependent

SOLIDWORKS allocates command and flyout IDs in creation order, at runtime. Two
consequences:

1. **Never persist tab contents across sessions.** A rescan mid-session shifts the
   numbering, so stored IDs go stale and most buttons vanish on the next launch.
   The tab is created once and its boxes are cleared and refilled from live IDs on
   every build.
2. **Never reuse a group or flyout UserID within one session.** Re-creating one
   whose ID was removed earlier returns `null`. A generation counter advances on
   every rebuild and feeds the base IDs.

### Command groups are never released — a SOLIDWORKS limitation

**Tools > Customize > Toolbars gains a row on every rebuild, and nothing removes
it.** A restart collapses them to one.

`ICommandGroup` has **no method to remove command items**, so a rebuild cannot
update a group in place — it must create a new one, and every created group adds a
row. `RemoveCommandGroup2` returns success without releasing the group:
`NumberOfGroups` climbs 1, 2, 3, 4 as groups are created and never comes back down.

Four approaches were measured, all failed:

| Tried | Result |
|---|---|
| `RemoveCommandGroup2(id, RuntimeOnly=false)` | No effect on the rows |
| Clearing `HasToolbar` before removing | No effect |
| A fixed group ID, so only one group exists | **Crashed SOLIDWORKS twice**, with missing macros and wrong icons first |
| `HasToolbar = false` on rebuilt groups | Stops the rows — and **empties the tab of every plain button** |

That last one is the trap worth understanding, because it works at what it aims
at. A command group with no toolbar **does not hand out usable command IDs**, so
`ICommandTabBox::AddCommands` silently ignores them. Drop-downs survive, because a
flyout does not depend on the command group — which makes it look like a
drop-down-only bug and sent the first diagnosis the wrong way.

**Accepted as a limitation.** Cosmetic, bounded by rebuilds in one session, clears
on restart. Do not spend more time on it without new evidence.

### A dragged toolbar loses its position when the library changes

**Open, and not solved.** Position now survives restarts while the macro set is
unchanged; changing the library still resets it and can leave a dead greyed-out
button.

Two flags interact. `RemoveCommandGroup2`'s `RuntimeOnly` must be **true**, or the
toolbar's registry entry — where SOLIDWORKS stores the docked position — is
deleted. `CreateCommandGroup2`'s `IgnorePreviousVersion` tells SOLIDWORKS to
discard everything it saved about the group, including that position, so it is now
asked only when the enabled macro set has actually changed, keyed on a hash stored
in `settings.json`.

**Four confident diagnoses have been wrong.** Anyone picking this up should
instrument before theorising — log the generation, the group ID, the computed
versus stored signature, and the resulting flag. Nothing currently logged
distinguishes the hypotheses.

### Flyouts

- A flyout's open-callback fires on **every click** and must rebuild its items
  right then. A no-op callback produces *"An invalid argument was encountered"*.
- Flyout buttons on a command tab need `swCommandTabButton_ActionFlyout`, not
  `SimpleFlyout`.
- **A flyout cannot be placed on a classic toolbar.** `CreateFlyoutGroup2` gives
  exactly two destinations: a command tab, and context menus. So the classic
  toolbar is flattened — every enabled macro gets a command in the group, and a
  drop-down's macros appear inline. The tab and the toolbar are filled by
  different routes, which is what makes that possible.

### Tab focus

Removing and re-adding the tab makes SOLIDWORKS treat it as new and auto-activate
it — which caused two separate bugs. **Never remove the tab**, and double-buffer a
rescan: create the new commands and add the new boxes *before* removing the old.

## Reading a macro's version out of a `.swp`

**Do not raw-scan the file.** A `.swp` is an OLE compound file whose free sectors
retain earlier copies of the source, so a scan finds old and new versions side by
side with no way to tell which is live. This produced a false result once.

Open it as structured storage via `StgOpenStorage` / `IStorage` / `IStream`, which
respects the declared stream length so slack is never visible, then decompress the
module stream (MS-OVBA, RLE with a sliding window).

**The trap worth remembering:** the `PROJECTVERSION` record in the `dir` stream
declares a size of 4 but carries 6 bytes. Believing the size field puts the record
walk two bytes out of step and turns everything after it into garbage. A smoke test
fails if the correction is removed.

## Asking GitHub for the latest release

- GitHub answers **403 without a `User-Agent`**.
- **TLS 1.2 must be enabled explicitly** — .NET Framework defaults can be too old
  and fail confusingly.
- **An uncomparable tag must be reported as unknown, not as "up to date."** WiX
  tags a release `wix3141rtm`, which parsed to 0.0.0 and was reported as up to
  date — a false reassurance.
- `System.Threading` must not be imported into `MacroDeckAddin.cs`, or every bare
  `Timer` becomes ambiguous.

## Other findings

- Getting back onto the SOLIDWORKS thread matters: UI work from a background
  thread fails in ways that look like unrelated API errors.
- Drop-down entries **cannot show hover tooltips**; their text appears in the
  status bar instead.
- The CommandManager **does not scroll** — a toolbar wider than the window is
  clipped. Mitigate with drop-down grouping and shorter names.
- Icon-to-text proportion is not controllable by an add-in.
- BMP icons treat the top-left pixel as transparent; PNGs use real alpha.
- Where the API documentation is ambiguous, **the installed type library is ground
  truth** — it is what the VBA editor itself reads.

## Known limitations

- A dragged classic toolbar loses its position when the library changes (above).
- Tools > Customize > Toolbars gains a row per rebuild (above).
- The installer is unsigned, so SmartScreen warns on every machine.
- `.msi` files cannot carry a custom file icon; only Add/Remove Programs shows one.
- Nested or overlapping library folders would produce duplicate buttons; only
  exact-duplicate paths are rejected.
