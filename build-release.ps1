# Builds dist\Paddock-Setup-<version>.exe (per-user installer, no admin) and dist\Paddock-<version>-portable.zip
param([string]$Version = "0.1.0")
$ErrorActionPreference = "Stop"
Set-Location $PSScriptRoot
Remove-Item dist -Recurse -Force -ErrorAction SilentlyContinue
dotnet publish Paddock.csproj -c Release -r win-x64 --self-contained true `
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
  -p:DebugType=None -p:Version=$Version -o dist\publish
if ($LASTEXITCODE) { throw "publish failed" }
Compress-Archive dist\publish\Paddock.exe "dist\Paddock-$Version-portable.zip"
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe") | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found (winget install JRSoftware.InnoSetup --scope user)" }
& $iscc "/DAppVersion=$Version" installer\paddock.iss
if ($LASTEXITCODE) { throw "installer failed" }
Get-ChildItem dist -File | Select-Object Name, @{n='MB';e={[math]::Round($_.Length/1MB,1)}}
