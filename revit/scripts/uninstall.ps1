<#
.SYNOPSIS
  Unregisters Family Studio from Revit for the current Windows user.

.DESCRIPTION
  Removes %APPDATA%\Autodesk\Revit\Addins\<year>\FamilyStudio.addin. The build folder, the
  ChatGPT sign-in and your session folders are left alone.

.EXAMPLE
  .\scripts\uninstall.ps1 -RevitYear 2026
#>
param(
    [ValidateSet("2025", "2026", "2027")]
    [string]$RevitYear = "2026"
)

$ErrorActionPreference = "Stop"
$manifest = Join-Path $env:APPDATA "Autodesk\Revit\Addins\$RevitYear\FamilyStudio.addin"
if (Test-Path $manifest) {
    Remove-Item $manifest
    Write-Host "Removed $manifest. Restart Revit to unload Family Studio." -ForegroundColor Green
} else {
    Write-Host "Family Studio is not registered for Revit $RevitYear."
}
