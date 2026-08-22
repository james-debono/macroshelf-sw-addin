# Compiles and runs the offline smoke tests (everything that works without
# SolidWorks: library scanning, settings persistence, icon generation).
# Exits non-zero if any check fails.
$ErrorActionPreference = "Stop"

$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$src  = Join-Path (Split-Path -Parent $here) "src"
$out  = Join-Path $here "bin"
$csc  = Join-Path $env:windir "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

New-Item -ItemType Directory -Force -Path $out | Out-Null

& $csc /nologo /target:exe "/out:$out\SmokeTest.exe" `
    /reference:System.dll `
    /reference:System.Core.dll `
    /reference:System.Drawing.dll `
    /reference:System.Web.Extensions.dll `
    (Join-Path $here "SmokeTest.cs") `
    (Join-Path $src "LibraryScanner.cs") `
    (Join-Path $src "IconFactory.cs") `
    (Join-Path $src "Settings.cs") `
    (Join-Path $src "SwpVersionReader.cs") `
    (Join-Path $src "UpdateChecker.cs")
if ($LASTEXITCODE -ne 0) { throw "Test compilation failed" }

& "$out\SmokeTest.exe"
$failures = $LASTEXITCODE
if ($failures -ne 0) {
    Write-Host "$failures check(s) failed" -ForegroundColor Red
    exit $failures
}
Write-Host "All checks passed" -ForegroundColor Green
