# Servis Hakediş — tek dosyalık kurulum (ServisHakedis_Kurulum.exe) hazırlar.
#   powershell -ExecutionPolicy Bypass -File installer\kurulum-hazirla.ps1
# 1) Programı .NET'siz bilgisayarda da çalışacak şekilde (self-contained, win-x64) yayınlar.
# 2) Çıktıda .env / API anahtarı OLMADIĞINI denetler (geliştiricinin anahtarı kuruluma girerse durur).
# 3) Windows'un kendi aracı IExpress ile tek bir Kurulum.exe üretir (ek program gerekmez).
$ErrorActionPreference = 'Stop'
$root    = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\HakedisOtomasyon.Web\HakedisOtomasyon.Web.csproj'
$out     = Join-Path $PSScriptRoot 'out'
$publish = Join-Path $out 'publish'
$stage   = Join-Path $out 'stage'
$setup   = Join-Path $out 'ServisHakedis_Kurulum.exe'

if (Test-Path $out) { Remove-Item $out -Recurse -Force }
New-Item -ItemType Directory -Path $publish, $stage -Force | Out-Null

Write-Host '1/3 Yayınlanıyor (Release, win-x64, self-contained)...'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publish /p:DebugType=None /p:DebugSymbols=false /p:AppendTargetFrameworkToOutputPath=false
if ($LASTEXITCODE -ne 0) { throw 'dotnet publish başarısız.' }

Write-Host '2/3 API anahtarı denetimi...'
$leak = Get-ChildItem $publish -Recurse -Force -Filter '.env*' | Where-Object { $_.Name -ne '.env.example' }
if ($leak) { throw "Kuruluma .env girdi, durduruldu: $($leak.FullName -join ', ')" }
# geliştiricinin gerçek anahtar değerleri çıktıdaki hiçbir dosyada geçmemeli
$devEnv = Join-Path $root 'src\HakedisOtomasyon.Web\bin\Release\net8.0-windows\.env'
$secrets = @()
if (Test-Path $devEnv) {
    $secrets = Get-Content $devEnv | Where-Object { $_ -match '^\s*(GEMINI_API_KEY|AZURE_DI_KEY|OPENAI_API_KEY)\s*=\s*(\S{12,})' } |
               ForEach-Object { ($_ -split '=', 2)[1].Trim() } | Where-Object { $_ -notmatch '^BURAYA_' }
}
foreach ($s in $secrets) {
    $hit = Get-ChildItem $publish -Recurse -File | Select-String -SimpleMatch -Pattern $s -List -ErrorAction SilentlyContinue
    if ($hit) { throw "Kurulumda API anahtarı bulundu, durduruldu: $($hit.Path -join ', ')" }
}
Write-Host '   Temiz: kurulumda .env ya da anahtar yok.'

Write-Host '3/3 Kurulum.exe oluşturuluyor (IExpress)...'
Compress-Archive -Path (Join-Path $publish '*') -DestinationPath (Join-Path $stage 'ServisHakedis.zip') -CompressionLevel Optimal
Copy-Item (Join-Path $PSScriptRoot 'install.cmd'), (Join-Path $PSScriptRoot 'install.ps1') $stage

$sed = Join-Path $out 'kurulum.sed'
@"
[Version]
Class=IEXPRESS
SEDVersion=3
[Options]
PackagePurpose=InstallApp
ShowInstallProgramWindow=0
HideExtractAnimation=0
UseLongFileName=1
InsideCompressed=0
CAB_FixedSize=0
CAB_ResvCodeSigning=0
RebootMode=N
InstallPrompt=%InstallPrompt%
DisplayLicense=%DisplayLicense%
FinishMessage=%FinishMessage%
TargetName=%TargetName%
FriendlyName=%FriendlyName%
AppLaunched=%AppLaunched%
PostInstallCmd=%PostInstallCmd%
AdminQuietInstCmd=%AdminQuietInstCmd%
UserQuietInstCmd=%UserQuietInstCmd%
SourceFiles=SourceFiles
[Strings]
InstallPrompt=Servis Hakedis kurulsun mu?
DisplayLicense=
FinishMessage=
TargetName=$setup
FriendlyName=Servis Hakedis Kurulumu
AppLaunched=cmd /c install.cmd
PostInstallCmd=<None>
AdminQuietInstCmd=
UserQuietInstCmd=
FILE0="install.cmd"
FILE1="install.ps1"
FILE2="ServisHakedis.zip"
[SourceFiles]
SourceFiles0=$stage\
[SourceFiles0]
%FILE0%=
%FILE1%=
%FILE2%=
"@ | Set-Content -Path $sed -Encoding ASCII

Start-Process -FilePath "$env:WINDIR\System32\iexpress.exe" -ArgumentList '/N', '/Q', $sed -Wait
if (-not (Test-Path $setup)) { throw 'IExpress kurulum dosyasını oluşturamadı.' }
Write-Host ("Hazır: {0} ({1:N0} MB)" -f $setup, ((Get-Item $setup).Length / 1MB))
