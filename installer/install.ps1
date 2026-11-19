# Servis Hakediş — kurulum (Kurulum.exe içinden çalışır; yönetici izni gerekmez)
#  • Program %LOCALAPPDATA%\Programs\ServisHakedis klasörüne açılır (eski sürüm varsa üzerine yazılır; veriler ve ayarlar korunur).
#  • Masaüstü ve Başlat menüsü kısayolu oluşturulur, program açılır.
#  • API anahtarı GELMEZ: firma kendi anahtarını programda Ayarlar → Yapay Zekâ (Form Okuma) bölümünden girer.
$ErrorActionPreference = 'Stop'
$here    = Split-Path -Parent $MyInvocation.MyCommand.Path
$target  = Join-Path $env:LOCALAPPDATA 'Programs\ServisHakedis'
$payload = Join-Path $here 'ServisHakedis.zip'

Add-Type -AssemblyName PresentationFramework
try {
    Get-Process ServisHakedis -ErrorAction SilentlyContinue | ForEach-Object { $_.CloseMainWindow() | Out-Null; $_.WaitForExit(10000) | Out-Null }
    Get-Process ServisHakedis -ErrorAction SilentlyContinue | Stop-Process -Force

    # Önceki kurulumda kullanıcının girdiği ayarlar (.env) korunur
    $envFile = Join-Path $target '.env'
    $keep = if (Test-Path $envFile) { Get-Content $envFile -Raw } else { $null }

    if (Test-Path $target) { Remove-Item $target -Recurse -Force }
    New-Item -ItemType Directory -Path $target -Force | Out-Null
    Expand-Archive -Path $payload -DestinationPath $target -Force
    if ($keep) { Set-Content -Path $envFile -Value $keep -Encoding UTF8 -NoNewline }

    $exe   = Join-Path $target 'ServisHakedis.exe'
    $shell = New-Object -ComObject WScript.Shell
    foreach ($dir in @([Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('Programs')) ''))) {
        $lnk = $shell.CreateShortcut((Join-Path $dir 'Servis Hakediş.lnk'))
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = $target
        $lnk.IconLocation = "$exe,0"
        $lnk.Description = 'Servis Hakediş Otomasyonu'
        $lnk.Save()
    }

    [System.Windows.MessageBox]::Show(
        "Servis Hakediş kuruldu.`n`nKlasör: $target`nMasaüstüne kısayol eklendi.`n`nOtomatik doldur için yapay zekâ anahtarınızı programda`nAyarlar → Yapay Zekâ (Form Okuma) bölümünden girin.",
        'Servis Hakediş Kurulumu', 'OK', 'Information') | Out-Null
    Start-Process -FilePath $exe -WorkingDirectory $target
}
catch {
    [System.Windows.MessageBox]::Show("Kurulum tamamlanamadı:`n$($_.Exception.Message)", 'Servis Hakediş Kurulumu', 'OK', 'Error') | Out-Null
    exit 1
}
