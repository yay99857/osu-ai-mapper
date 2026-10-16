# Builds osu-ai-mapper-setup.exe: the desktop app published self-contained for win-x64, zipped and
# carried inside a single-file installer, with this repository and uv as the tools it points at.
#
#   pwsh -File tools/installer/build.ps1 [-Output artifacts/installer]
param(
    [string]$Output = "artifacts/installer"
)
$ErrorActionPreference = "Stop"
$repo = (Resolve-Path "$PSScriptRoot/../..").Path
$out = Join-Path $repo $Output
$app = Join-Path $out "app"
$zip = Join-Path $out "payload.zip"
$settings = Join-Path $out "install.json"

if (Test-Path $out) { Remove-Item -Recurse -Force $out }
New-Item -ItemType Directory -Force $out | Out-Null

Write-Host "Publishing the desktop app (self-contained, win-x64)..."
dotnet publish "$repo/apps/desktop/OsuAi.Desktop.csproj" -c Release -r win-x64 --self-contained -o $app -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "desktop publish failed" }
Get-ChildItem $app -Filter *.pdb | Remove-Item
Compress-Archive -Path "$app/*" -DestinationPath $zip -CompressionLevel Optimal

$uv = (Get-Command uv -ErrorAction SilentlyContinue)?.Source
$commit = (git -C $repo rev-parse --short HEAD 2>$null)
$version = "{0}+{1}" -f (Get-Date -Format "yyyy.M.d"), $commit
@{ repositoryRoot = $repo; uv = $uv; version = $version } | ConvertTo-Json | Set-Content -Encoding utf8 $settings

Write-Host "Building the installer..."
dotnet publish "$repo/tools/installer/OsuAi.Installer.csproj" -c Release -r win-x64 --self-contained `
    -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:PayloadZip=$zip -p:InstallSettings=$settings -o (Join-Path $out "setup") -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "installer publish failed" }

$setup = Join-Path $out "osu-ai-mapper-setup.exe"
Move-Item (Join-Path $out "setup/osu-ai-mapper-setup.exe") $setup
Remove-Item -Recurse -Force (Join-Path $out "setup"), $app
Write-Host ("Done: {0} ({1:N0} MB), version {2}" -f $setup, ((Get-Item $setup).Length / 1MB), $version)
