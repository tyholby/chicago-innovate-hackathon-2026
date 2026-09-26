<#
.SYNOPSIS
  Builds Family Studio and registers it with Revit for the current Windows user.

.DESCRIPTION
  Writes %APPDATA%\Autodesk\Revit\Addins\<year>\FamilyStudio.addin, pointing straight at the
  add-in in this checkout's build folder. Nothing is copied into Revit's program folders and no
  administrator rights are needed. Revit picks up a new .addin even while it is running, but a
  rebuilt DLL only loads after Revit restarts.

.EXAMPLE
  .\scripts\install.ps1                      # build and install for Revit 2026
  .\scripts\install.ps1 -RevitYear 2025
  .\scripts\install.ps1 -NoBuild             # register the existing build only
  .\scripts\install.ps1 -Launch              # then start Revit
#>
param(
    [ValidateSet("2025", "2026", "2027")]
    [string]$RevitYear = "2026",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$NoBuild,
    [switch]$Launch
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot "build.ps1") -RevitYear $RevitYear -Configuration $Configuration
}

$dll = Join-Path $root "src\FamilyStudio.Revit\bin\$Configuration\$RevitYear\FamilyStudio.Revit.dll"
if (-not (Test-Path $dll)) { throw "No build found at $dll. Run without -NoBuild." }
$dll = (Resolve-Path $dll).Path

$addinFolder = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear"
New-Item -ItemType Directory -Force -Path $addinFolder | Out-Null
$manifest = Join-Path $addinFolder "FamilyStudio.addin"

$xml = @"
<?xml version="1.0" encoding="utf-8"?>
<RevitAddIns>
  <AddIn Type="Application">
    <Name>Family Studio</Name>
    <Assembly>$dll</Assembly>
    <AddInId>016949e7-724a-4c46-973f-0899c23f1739</AddInId>
    <FullClassName>FamilyStudio.Revit.App</FullClassName>
    <VendorId>FamilyStudio</VendorId>
    <VendorDescription>Family Studio, Chicago Innovate Hackathon 2026</VendorDescription>
  </AddIn>
</RevitAddIns>
"@
Set-Content -Path $manifest -Value $xml -Encoding UTF8
Write-Host "Registered: $manifest" -ForegroundColor Green
Write-Host "Add-in:     $dll"

if ($Launch) {
    $revit = Join-Path $env:ProgramFiles "Autodesk\Revit $RevitYear\Revit.exe"
    if (Test-Path $revit) {
        Write-Host "Starting Revit $RevitYear..." -ForegroundColor Cyan
        Start-Process $revit
    } else {
        Write-Warning "Revit $RevitYear was not found at $revit. Start it yourself."
    }
} else {
    Write-Host ""
    Write-Host "Start Revit $RevitYear. The Family Studio tab appears on the ribbon."
}
