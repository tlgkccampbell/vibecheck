<#
.SYNOPSIS
Packs the vibecheck tool from this working tree and reinstalls it as a global .NET tool.

.DESCRIPTION
Each run packs a version of its own, the project's version with a "-dev." suffix and a UTC timestamp, so that
NuGet can never hand back an older package cached under the same version. Old packages in nupkg/ are deleted
first, and the installed tool is uninstalled before the new one is installed. A running vibecheck process holds the
installed files open, so the script refuses to run while one exists.
#>
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $true

$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src/Vibecheck.Cli/Vibecheck.Cli.csproj'
$packages = Join-Path $root 'nupkg'

if (Get-Process -Name vibecheck -ErrorAction SilentlyContinue) {
    throw 'vibecheck is running; stop it (Ctrl+C in its terminal) before reinstalling.'
}

$base = (dotnet msbuild $project -getProperty:Version).Trim()
$version = "$base-dev.$((Get-Date).ToUniversalTime().ToString('yyyyMMddHHmmss'))"

if (Test-Path $packages) {
    Get-ChildItem $packages -Filter 'vibecheck.*.nupkg' | Remove-Item
}
Write-Host "packing vibecheck $version"
dotnet pack $project -c Release -o $packages "-p:Version=$version" -v q --nologo

if (dotnet tool list --global | Select-String -Pattern '^vibecheck\s' -Quiet) {
    dotnet tool uninstall --global vibecheck
}
dotnet tool install --global vibecheck --version $version --configfile (Join-Path $root 'tool.nuget.config')
