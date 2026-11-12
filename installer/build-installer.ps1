# Builds OpenPedalControl-Setup-<version>.exe into installer\output.
# Requires the .NET SDK and Inno Setup 6 (ISCC.exe). Used by GitHub Actions and for local builds.
param(
    [string]$Version = "0.0.0",
    [string]$Iscc = ""
)
$ErrorActionPreference = "Stop"

# Official vJoy release, pinned by hash so a changed download never ends up in the installer.
$VJoyUrl = "https://github.com/BrunnerInnovation/vJoy/releases/download/v2.2.2.0/vJoySetup_v2.2.2.0_Win10_Win11.exe"
$VJoySha256 = "EF569A3105CD301B89580F18F60C66B339E95296ACF2C0DFCAF4B4BBF8AB68FE"
$VJoyLicenseUrl = "https://raw.githubusercontent.com/BrunnerInnovation/vJoy/v2.2.2.0/LICENSE.txt"

$root = Split-Path $PSScriptRoot -Parent
$staging = Join-Path $PSScriptRoot "staging"
New-Item -ItemType Directory -Force $staging | Out-Null

Write-Host "Building OpenPedalControl $Version"
dotnet build (Join-Path $root "src\OpenPedalControl\OpenPedalControl.csproj") -c Release -p:Version=$Version -nologo -v q
if ($LASTEXITCODE -ne 0) { throw "dotnet build failed" }

Copy-Item (Join-Path $root "src\OpenPedalControl\bin\Release\net48\OpenPedalControl.exe") $staging -Force
Copy-Item (Join-Path $root "LICENSE") $staging -Force
Copy-Item (Join-Path $root "README.md") $staging -Force

$vjoy = Join-Path $staging "vJoySetup.exe"
if (-not (Test-Path $vjoy) -or (Get-FileHash $vjoy -Algorithm SHA256).Hash -ne $VJoySha256) {
    Write-Host "Downloading vJoy"
    Invoke-WebRequest -UseBasicParsing -Uri $VJoyUrl -OutFile $vjoy
}
$hash = (Get-FileHash $vjoy -Algorithm SHA256).Hash
if ($hash -ne $VJoySha256) { Remove-Item $vjoy; throw "vJoy installer hash mismatch: $hash" }
Invoke-WebRequest -UseBasicParsing -Uri $VJoyLicenseUrl -OutFile (Join-Path $staging "vJoy-LICENSE.txt")

if (-not $Iscc) {
    $Iscc = @(
        (Get-Command iscc.exe -ErrorAction SilentlyContinue).Source,
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "$env:ProgramFiles\Inno Setup 6\ISCC.exe",
        "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe"
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
}
if (-not $Iscc) { throw "Inno Setup 6 (ISCC.exe) not found" }

& $Iscc "/DAppVersion=$Version" (Join-Path $PSScriptRoot "OpenPedalControl.iss")
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Get-ChildItem (Join-Path $PSScriptRoot "output") -Filter "*.exe" | ForEach-Object { Write-Host "Built $($_.FullName)" }
