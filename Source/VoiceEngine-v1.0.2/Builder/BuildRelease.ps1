param([string]$Runtime='win-x64')
$ErrorActionPreference='Stop'

$Version='1.0.2'
$Root=(Resolve-Path (Join-Path $PSScriptRoot '..')).Path
$Tools=Join-Path $Root '.tools'
$Out=Join-Path $Root 'Output'
$Stage=Join-Path $Root 'Stage'
$Src=Join-Path $Root 'src\DaggerfallVoiceEngine'
$Assets=Join-Path $Root 'BuildAssets'
$NugetRoot=Join-Path $Tools 'nuget-packages'

New-Item -ItemType Directory -Force $Tools,$Out,$Stage,$Assets,$NugetRoot | Out-Null
$env:NUGET_PACKAGES=$NugetRoot
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_NOLOGO='1'

function Download([string]$Url,[string]$Path) {
  if (!(Test-Path $Path)) {
    Write-Host "Downloading $Url"
    Invoke-WebRequest -UseBasicParsing $Url -OutFile $Path
  }
}

# Private .NET SDK: players never need .NET installed.
$Dotnet=Join-Path $Tools 'dotnet\dotnet.exe'
if (!(Test-Path $Dotnet)) {
  $Installer=Join-Path $Tools 'dotnet-install.ps1'
  Download 'https://dot.net/v1/dotnet-install.ps1' $Installer
  & powershell -NoProfile -ExecutionPolicy Bypass -File $Installer -Channel 8.0 -InstallDir (Join-Path $Tools 'dotnet') -NoPath
  if($LASTEXITCODE -ne 0){ throw 'Private .NET SDK bootstrap failed' }
}

# Private uv: only used by the maintainer builder to convert Granite .pt -> .npy.
$Uv=Join-Path $Tools 'uv.exe'
if (!(Test-Path $Uv)) {
  $UvZip=Join-Path $Tools 'uv.zip'
  Download 'https://github.com/astral-sh/uv/releases/latest/download/uv-x86_64-pc-windows-msvc.zip' $UvZip
  Expand-Archive -Force $UvZip $Tools

  if (!(Test-Path $Uv)) {
    $found=Get-ChildItem $Tools -Recurse -File -Filter 'uv.exe' | Select-Object -First 1
    if (!$found) { throw 'Could not bootstrap uv.exe' }

    $foundPath=[System.IO.Path]::GetFullPath($found.FullName)
    $uvPath=[System.IO.Path]::GetFullPath($Uv)
    if(-not $foundPath.Equals($uvPath,[System.StringComparison]::OrdinalIgnoreCase)){
      Copy-Item -Force $found.FullName $Uv
    }
  }
}
if (!(Test-Path $Uv)) { throw "uv.exe is missing after bootstrap: $Uv" }

$Model=Join-Path $Assets 'kokoro.onnx'
Download 'https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro.onnx' $Model

$GranitePt=Join-Path $Assets 'am_granite.pt'
Download 'https://raw.githubusercontent.com/n33kos/kokoro-voices/main/voices/am_granite.pt' $GranitePt

$GraniteNpy=Join-Path $Assets 'am_granite.npy'
if (!(Test-Path $GraniteNpy)) {
  & $Uv run --python 3.12 --with 'torch>=2.2,<3' --with 'numpy>=1.26,<3' (Join-Path $PSScriptRoot 'convert_granite.py') $GranitePt $GraniteNpy
  if($LASTEXITCODE -ne 0){ throw 'Granite voice conversion failed' }
}
if (!(Test-Path $GraniteNpy)) { throw "Converted Granite voice is missing: $GraniteNpy" }

$Project=Join-Path $Src 'DaggerfallVoiceEngine.csproj'
$Publish=Join-Path $Stage 'publish'
$Package=Join-Path $Stage 'package'

if(Test-Path $Publish){Remove-Item -Recurse -Force $Publish}
if(Test-Path $Package){Remove-Item -Recurse -Force $Package}

& $Dotnet restore $Project
if($LASTEXITCODE -ne 0){throw 'dotnet restore failed'}

& $Dotnet publish $Project -c Release -r $Runtime --self-contained true -o $Publish
if($LASTEXITCODE -ne 0){throw 'dotnet publish failed'}

# KokoroSharp's NuGet target copies content/voices after Build, but a custom
# `dotnet publish -o` directory does not reliably receive those files.
# Use the builder's private NuGet cache as the authoritative source instead.
$heart=Get-ChildItem $NugetRoot -Recurse -File -Filter 'af_heart.npy' -ErrorAction SilentlyContinue |
  Where-Object { $_.FullName -match '[\\/]kokorosharp[\\/]' } |
  Select-Object -First 1

if(!$heart){ throw 'Could not locate KokoroSharp official voice catalog after restore (af_heart.npy missing).' }
$OfficialVoices=$heart.Directory.FullName
$OfficialCount=(Get-ChildItem $OfficialVoices -File -Filter '*.npy').Count
if($OfficialCount -lt 20){ throw "Official Kokoro voice catalog looks incomplete: only $OfficialCount .npy files found." }

$EngineDir=Join-Path $Package 'DaggerfallUnity_Data\StreamingAssets\DaggerfallVoiceEngine'
$ModsDir=Join-Path $Package 'DaggerfallUnity_Data\StreamingAssets\Mods'
$OfficialDest=Join-Path $EngineDir 'Assets\voices'
$CustomDest=Join-Path $EngineDir 'Assets\voices-custom'

New-Item -ItemType Directory -Force $EngineDir,$ModsDir,$OfficialDest,$CustomDest | Out-Null

Copy-Item -Recurse -Force (Join-Path $Publish '*') $EngineDir
Copy-Item -Force $Model (Join-Path $EngineDir 'Assets\kokoro.onnx')
Copy-Item -Recurse -Force (Join-Path $OfficialVoices '*') $OfficialDest
Copy-Item -Force $GraniteNpy (Join-Path $CustomDest 'am_granite.npy')
Copy-Item -Force (Join-Path $Src 'DaggerfallVoiceEngine.json') $EngineDir
Copy-Item -Recurse -Force (Join-Path $Root 'Licenses') (Join-Path $EngineDir 'Licenses')
Copy-Item -Force (Join-Path $Root 'VOICE-OVERRIDES.txt') (Join-Path $Package 'VOICE-OVERRIDES.txt')
Copy-Item -Force (Join-Path $Root 'CHANGELOG-1.0.2.txt') (Join-Path $Package 'CHANGELOG-Daggerfall-Voice-Engine.txt')

$LicenseDir=Join-Path $EngineDir 'Licenses'
Download 'https://raw.githubusercontent.com/Lyrcaxis/KokoroSharp/main/LICENSE' (Join-Path $LicenseDir 'KokoroSharp-MIT.txt')
Download 'https://raw.githubusercontent.com/microsoft/onnxruntime/main/LICENSE' (Join-Path $LicenseDir 'ONNX-Runtime-MIT.txt')
Download 'https://www.apache.org/licenses/LICENSE-2.0.txt' (Join-Path $LicenseDir 'Kokoro-Apache-2.0.txt')
Download 'https://creativecommons.org/publicdomain/zero/1.0/legalcode.txt' (Join-Path $LicenseDir 'Granite-CC0-1.0.txt')

Get-ChildItem (Join-Path $Root 'InputMods') -Filter '*.dfmod' -ErrorAction SilentlyContinue |
  Copy-Item -Destination $ModsDir -Force

$Exe=Join-Path $EngineDir 'Daggerfall Voice Engine.exe'
if(!(Test-Path $Exe)){ throw "Published engine missing: $Exe" }

# Structural checks before the runtime smoke test.
if(!(Test-Path (Join-Path $EngineDir 'Assets\kokoro.onnx'))){ throw 'Packaged kokoro.onnx missing' }
if(!(Test-Path (Join-Path $OfficialDest 'af_heart.npy'))){ throw 'Packaged official voice af_heart.npy missing' }
if(!(Test-Path (Join-Path $CustomDest 'am_granite.npy'))){ throw 'Packaged custom voice am_granite.npy missing' }

# Runtime self-test validates BOTH af_heart and am_granite.
& $Exe --self-test
if($LASTEXITCODE -ne 0){throw 'Daggerfall Voice Engine self-test failed'}

$Readme=Join-Path $Package 'README-Daggerfall-Voice-Engine.txt'
@'
DAGGERFALL VOICE ENGINE - PLAYER INSTALL

1. Extract this archive into your Daggerfall Unity folder and preserve folders.
2. Enable one or more Daggerfall Narrator modules in the launcher.
3. Launch Daggerfall Unity normally. The mods start Daggerfall Voice Engine automatically.

Expected engine location:
DaggerfallUnity_Data\StreamingAssets\DaggerfallVoiceEngine\

Players do NOT install Python, pip, .NET, eSpeak, uv, or Kokoro model/voice files separately.

Troubleshooting:
- http://127.0.0.1:5000/ shows basic engine status.
- http://127.0.0.1:5000/health shows health/queue status.
- Daggerfall Voice Engine.log is written beside the executable.
- Manually starting a second copy exits immediately when the hidden mod-launched instance is already running.
'@ | Set-Content -Encoding UTF8 $Readme

$Zip=Join-Path $Out "Daggerfall-Narrator-Daggerfall-Voice-Engine-$Runtime-v$Version.zip"
if(Test-Path $Zip){Remove-Item -Force $Zip}
Compress-Archive -Path (Join-Path $Package '*') -DestinationPath $Zip -CompressionLevel Optimal

Get-FileHash $Zip -Algorithm SHA256 |
  ForEach-Object { $_.Hash + '  ' + (Split-Path $Zip -Leaf) } |
  Set-Content "$Zip.sha256"

Write-Host ""
Write-Host "READY TO REDISTRIBUTE: $Zip" -ForegroundColor Green
Write-Host "Official Kokoro voices packaged: $OfficialCount" -ForegroundColor Green
