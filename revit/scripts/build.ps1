<#
.SYNOPSIS
  Builds the Family Studio Revit add-in for one Revit year.

.EXAMPLE
  .\scripts\build.ps1                      # Revit 2026, Release
  .\scripts\build.ps1 -RevitYear 2027      # Revit 2027 (needs the .NET 10 SDK)
  .\scripts\build.ps1 -Configuration Debug
#>
param(
    [ValidateSet("2025", "2026", "2027")]
    [string]$RevitYear = "2026",
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\FamilyStudio.Revit\FamilyStudio.Revit.csproj"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK is not installed. Install .NET 8 (Revit 2025 and 2026) or .NET 10 (Revit 2027) from https://dotnet.microsoft.com/download"
}
if ($RevitYear -eq "2027" -and -not ((dotnet --list-sdks) -match "^10\.")) {
    throw "Revit 2027 add-ins need the .NET 10 SDK. Install it from https://dotnet.microsoft.com/download"
}

$revitProcess = Get-Process -Name "Revit" -ErrorAction SilentlyContinue
if ($revitProcess) {
    Write-Warning "Revit is running. If this add-in is already loaded, Revit locks its DLL and the build cannot replace it. Close Revit first."
}

$envFile = Join-Path $root ".env"
if (-not (Test-Path $envFile)) {
    Write-Host "No revit\.env yet. Copy revit\.env.example to revit\.env to configure Family Studio (optional)." -ForegroundColor DarkYellow
}

Write-Host "Building Family Studio for Revit $RevitYear ($Configuration)..." -ForegroundColor Cyan
dotnet build $project -c $Configuration -p:RevitYear=$RevitYear
if ($LASTEXITCODE -ne 0) { throw "The build failed." }

$output = Join-Path $root "src\FamilyStudio.Revit\bin\$Configuration\$RevitYear"
Write-Host ""
Write-Host "Built: $output\FamilyStudio.Revit.dll" -ForegroundColor Green
