<#
.SYNOPSIS
  Builds the Family Studio Rhino plug-in (FamilyStudio.rhp) for Rhino 8.

.EXAMPLE
  .\scripts\build.ps1                    # Debug
  .\scripts\build.ps1 -Configuration Release
#>
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root "src\FamilyStudio.Rhino\FamilyStudio.Rhino.csproj"

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw "The .NET SDK is not installed. Install .NET 8 or later from https://dotnet.microsoft.com/download"
}
if (Get-Process -Name "Rhino" -ErrorAction SilentlyContinue) {
    Write-Warning "Rhino is running. If it loaded this plug-in, the .rhp is locked and the build cannot replace it. Close Rhino first."
}

Write-Host "Building Family Studio for Rhino 8 ($Configuration)..." -ForegroundColor Cyan
dotnet build $project -c $Configuration
if ($LASTEXITCODE -ne 0) { throw "The build failed." }

$output = Join-Path $root "src\FamilyStudio.Rhino\bin\$Configuration"
Write-Host ""
Write-Host "Built: $output\net7.0\FamilyStudio.rhp (Rhino 8 default runtime)" -ForegroundColor Green
Write-Host "       $output\net48\FamilyStudio.rhp (Rhino 8 with /netfx)"
