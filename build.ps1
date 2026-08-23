# Builds MacroShelf.dll with the C# compiler that ships with Windows and
# packages releases\MacroShelf-<version>.msi using the vendored WiX toolset.
# The version comes from AssemblyVersion in src\AssemblyInfo.cs.
$ErrorActionPreference = "Stop"

$root         = Split-Path -Parent $MyInvocation.MyCommand.Path
$src          = Join-Path $root "src"
$out          = Join-Path $root "build"
$obj          = Join-Path $out "obj"
$releases     = Join-Path $root "releases"
$wix          = Join-Path $root "tools\wix"
$installerDir = Join-Path $src "installer"
$csc          = Join-Path $env:windir "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

# SW 2022 interops (oldest installed version) so the add-in loads in 2022/2024/2025.
$interops = "C:\Program Files\SOLIDWORKS Corp\SOLIDWORKS\api\redist"
if (-not (Test-Path (Join-Path $interops "SolidWorks.Interop.sldworks.dll"))) {
    throw "SolidWorks interop DLLs not found at $interops"
}

# ----- WiX toolset -----
# Not committed: 116 MB of binaries. Fetched on first build and cached in tools\wix.
# Pinned to an exact release so builds stay reproducible.
$wixVersion = "3.14.1"
$wixUrl     = "https://github.com/wixtoolset/wix3/releases/download/wix3141rtm/wix314-binaries.zip"

if (-not (Test-Path (Join-Path $wix "candle.exe"))) {
    Write-Host "WiX toolset not found. Downloading $wixVersion..."
    New-Item -ItemType Directory -Force -Path $wix | Out-Null
    $zip = Join-Path $env:TEMP "wix314-binaries.zip"
    try {
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        Invoke-WebRequest -Uri $wixUrl -OutFile $zip -UseBasicParsing
        Add-Type -AssemblyName System.IO.Compression.FileSystem
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $wix)
        Remove-Item $zip -Force
    } catch {
        throw "Could not download the WiX toolset from $wixUrl - $($_.Exception.Message)"
    }
    Set-Content -Path (Join-Path $wix "SOURCE.txt") -Encoding utf8 -Value @(
        "WiX Toolset v$wixVersion portable binaries"
        "Downloaded by build.ps1 from:"
        $wixUrl
    )
    if (-not (Test-Path (Join-Path $wix "candle.exe"))) {
        throw "WiX download completed but candle.exe is missing from $wix"
    }
    Write-Host "WiX toolset ready."
}

# ----- compile the add-in -----
New-Item -ItemType Directory -Force -Path $out | Out-Null

# The SolidWorks interops are referenced straight from the local installation
# and deliberately NOT copied into build\. Keeping them out of the output folder
# means the installer cannot pick them up by accident. They are resolved on the
# user's own machine at run time instead - see src\InteropResolver.cs.
# swpublished is no longer referenced at all: ISwAddin is declared in that file.

$sources = Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName }

# Artwork embedded into the DLL. Every PNG in src\assets is embedded as
# "MacroShelf.<file name>", which is the name IconFactory asks for - so adding a
# new icon is a matter of dropping the file in and referencing it there, with
# no build change. Each has a drawn fallback, so a missing file is not fatal.
$assetsDir = Join-Path $src "assets"
$resourceArgs = @()
if (Test-Path $assetsDir) {
    foreach ($art in Get-ChildItem $assetsDir -Filter *.png | Sort-Object Name) {
        $resourceArgs += "/resource:$($art.FullName),MacroShelf.$($art.Name)"
        Write-Host "  embedding MacroShelf.$($art.Name)"
    }
}

$cscArgs = @(
    "/nologo", "/target:library", "/platform:anycpu", "/optimize+",
    "/out:$out\MacroShelf.dll",
    "/reference:$interops\SolidWorks.Interop.sldworks.dll",
    "/reference:$interops\SolidWorks.Interop.swconst.dll",
    "/reference:System.dll",
    "/reference:System.Core.dll",
    "/reference:System.Drawing.dll",
    "/reference:System.Windows.Forms.dll",
    "/reference:System.Web.Extensions.dll"
) + $resourceArgs + $sources

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Compilation failed" }

# ----- read the version -----
$assemblyInfo = Get-Content (Join-Path $src "AssemblyInfo.cs") -Raw
if ($assemblyInfo -notmatch 'AssemblyVersion\("(\d+)\.(\d+)\.(\d+)\.(\d+)"\)') {
    throw "Could not read AssemblyVersion from AssemblyInfo.cs"
}
$asmVer  = "$($Matches[1]).$($Matches[2]).$($Matches[3]).$($Matches[4])"
$prodVer = "$($Matches[1]).$($Matches[2]).$($Matches[3])"

# An MSI ProductVersion carries only three fields, so every test build of a given
# version looks identical in Add/Remove Programs. The fourth field distinguishes
# them, so it goes in the file name: a release is MacroShelf-0.7.0.msi, a test build
# is MacroShelf-0.7.0.2.msi. Set the revision back to 0 when cutting the release.
$revision = [int]$Matches[4]
if ($revision -gt 0) {
    $fileVer = "$prodVer.$revision"
    Write-Host "Test build $asmVer (product version $prodVer)" -ForegroundColor Yellow
} else {
    $fileVer = $prodVer
}

# ----- package the MSI -----
New-Item -ItemType Directory -Force -Path $obj | Out-Null
New-Item -ItemType Directory -Force -Path $releases | Out-Null

& (Join-Path $wix "candle.exe") -nologo -arch x64 `
    "-dBinDir=$out" `
    "-dInstallerDir=$installerDir" `
    "-dProductVersion=$prodVer" `
    "-dAssemblyVersion=$asmVer" `
    -out "$obj\Product.wixobj" `
    (Join-Path $installerDir "Product.wxs")
if ($LASTEXITCODE -ne 0) { throw "candle failed" }

$msi = Join-Path $releases "MacroShelf-$fileVer.msi"
& (Join-Path $wix "light.exe") -nologo -ext WixUIExtension -spdb `
    -sice:ICE38 -sice:ICE57 -sice:ICE64 `
    -out $msi "$obj\Product.wixobj"
if ($LASTEXITCODE -ne 0) { throw "light failed" }

Write-Host "Built: $msi"
