<#
.SYNOPSIS
  Installs Family Studio into Rhino 8 as a local package, so it loads every time Rhino starts.

.DESCRIPTION
  Builds a Release .rhp, stages it with manifest.yml, packs it with Rhino's own Yak tool and
  installs that package from the local file. Nothing is published to the Rhino package server.
  Use -Uninstall to remove it again.

  Do not also load the plug-in from the build folder (run.ps1 or drag and drop) while the package
  is installed: both use the same plug-in ID and Rhino refuses the second with "Id already in use".

.EXAMPLE
  .\scripts\install.ps1
  .\scripts\install.ps1 -Uninstall
#>
param(
    [switch]$Uninstall
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$yak = Join-Path $env:ProgramFiles "Rhino 8\System\Yak.exe"
if (-not (Test-Path $yak)) { throw "Rhino 8's Yak tool was not found at $yak. Is Rhino 8 installed?" }

if ($Uninstall) {
    & $yak uninstall FamilyStudio
    Write-Host "Uninstalled. Restart Rhino." -ForegroundColor Green
    return
}

& (Join-Path $PSScriptRoot "build.ps1") -Configuration Release

$stage = Join-Path $root "artifacts\stage"
$artifacts = Join-Path $root "artifacts"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
New-Item -ItemType Directory -Force -Path $stage | Out-Null

# Ship only what Rhino needs: the .rhp and its runtime dependencies, without symbols.
$built = Join-Path $root "src\FamilyStudio.Rhino\bin\Release\net7.0"
Get-ChildItem $built -File | Where-Object { $_.Extension -notin ".pdb" } | Copy-Item -Destination $stage
if (Test-Path (Join-Path $built "runtimes")) { Copy-Item (Join-Path $built "runtimes") -Destination $stage -Recurse }
Copy-Item (Join-Path $root "manifest.yml") -Destination $stage

Push-Location $stage
try {
    & $yak build --platform win
    if ($LASTEXITCODE -ne 0) { throw "yak build failed." }
    $package = Get-ChildItem $stage -Filter *.yak | Select-Object -First 1
    Move-Item $package.FullName -Destination $artifacts -Force
} finally {
    Pop-Location
}

$package = Get-ChildItem $artifacts -Filter *.yak | Sort-Object LastWriteTime -Descending | Select-Object -First 1
& $yak install $package.FullName
if ($LASTEXITCODE -ne 0) { throw "yak install failed." }
Write-Host ""
Write-Host "Installed $($package.Name). Start Rhino 8 and run the command: FamilyStudio" -ForegroundColor Green
