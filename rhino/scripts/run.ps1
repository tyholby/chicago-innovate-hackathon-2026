<#
.SYNOPSIS
  Builds Family Studio and starts Rhino 8 with the plug-in loaded straight from the build folder.

.DESCRIPTION
  This is the development loop McNeel's own templates use: RHINO_PACKAGE_DIRS tells Rhino to load
  the plug-ins it finds in the build folder at startup. The variable is set for this Rhino process
  only, so nothing is installed, copied or written to the registry. Close Rhino, change code and
  run this again.

.EXAMPLE
  .\scripts\run.ps1                 # Debug build, .NET runtime (the Rhino 8 default)
  .\scripts\run.ps1 -Runtime netfx  # the .NET Framework runtime
  .\scripts\run.ps1 -NoBuild
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [ValidateSet("netcore", "netfx")]
    [string]$Runtime = "netcore",
    [switch]$NoBuild
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot

if (-not $NoBuild) {
    & (Join-Path $PSScriptRoot "build.ps1") -Configuration $Configuration
}

$rhino = Join-Path $env:ProgramFiles "Rhino 8\System\Rhino.exe"
if (-not (Test-Path $rhino)) { throw "Rhino 8 was not found at $rhino." }

# Rhino picks net7.0 (or net48 under /netfx) out of this folder by itself.
$packageDir = (Resolve-Path (Join-Path $root "src\FamilyStudio.Rhino\bin\$Configuration")).Path
$env:RHINO_PACKAGE_DIRS = $packageDir

Write-Host "Starting Rhino 8 with Family Studio from $packageDir" -ForegroundColor Cyan
Write-Host "In Rhino, run the command: FamilyStudio"
Start-Process $rhino -ArgumentList "/nosplash", "/$Runtime"
