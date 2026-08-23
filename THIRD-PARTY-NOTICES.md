# Third-party notices

MacroShelf itself is © 2026 James Debono and released under the MIT licence — see
[LICENSE](LICENSE).

The MIT licence covers **MacroShelf's own code only**. The components below belong
to their respective owners. Listing them here does not place them under the MIT
licence.

**This repository and the installer contain no third-party code.** MacroShelf is
one assembly, `MacroShelf.dll`, and that is all the MSI installs.

---

## SOLIDWORKS API interop assemblies — used, not included

**Owner:** Dassault Systèmes SolidWorks Corporation

MacroShelf is built against three assemblies from a local SOLIDWORKS
installation's `…\SOLIDWORKS\api\redist\` folder:

| File | Purpose |
|---|---|
| `SolidWorks.Interop.sldworks.dll` | the main SOLIDWORKS API |
| `SolidWorks.Interop.swconst.dll` | API enumerations and constants |
| `SolidWorks.Interop.swpublished.dll` | previously, the add-in interface |

**They are referenced at build time only.** At run time MacroShelf loads them from
the SOLIDWORKS installation already present on the machine — the same installation
the add-in exists to talk to. No copy of any of these files is contained in this
repository, in the installer, or in any release.

`swpublished.dll` is not needed, even at build time. The `ISwAddin` interface is
declared in `src/InteropResolver.cs` from its published IID and signatures.

### Why they are resolved at run time

Anyone running MacroShelf necessarily has SOLIDWORKS installed, so the assemblies
are always already on the machine — and resolving them there is the only approach
that actually works across releases.

The assemblies are strong-named and their version moves with each SOLIDWORKS
release (30.5.0.49 for 2022, 32.5.0.48 for 2024, 33.5.0.53 for 2025). MacroShelf is
compiled against the oldest so that a single build serves every supported release,
and ordinary strong-name binding demands an exact version match — so a fixed
reference to one version cannot load another. Resolving through
`AppDomain.AssemblyResolve` sidesteps the version check, which is correct here
because the COM interfaces underneath are stable.

## WiX Toolset — build-time only, not redistributed

**Owner:** .NET Foundation and contributors
**Licence:** [Microsoft Reciprocal Licence (MS-RL)](https://opensource.org/licenses/ms-rl)

`build.ps1` downloads WiX 3.14 to `tools\wix\` on the first build, to package the
MSI. It is **not** in this repository and **not** in the installer — it is a build
tool that runs on the developer's machine. The download URL is pinned in
`build.ps1` so builds stay reproducible.

## CodeStack — reference only, not included

**Owner:** Xarial Pty Limited
**Licence:** MIT

The [CodeStack](https://www.codestack.net/) SOLIDWORKS API examples were consulted
during development. No CodeStack code is included in this repository.
