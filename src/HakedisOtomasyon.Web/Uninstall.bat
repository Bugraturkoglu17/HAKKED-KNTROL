@echo off
chcp 65001 >nul
title Servis Hakediş Kaldırma Aracı

echo.
echo ========================================
echo   SERVIS HAKEDIS KOMPLE KALDIRMA
echo ========================================
echo.
echo Bu islem uygulamaya ait tum verileri kalici olarak silecektir:
echo.
echo - Uygulama dosyalari
echo - HAKEDIS DATABASE klasoru
echo - Magaza listesi
echo - Birim fiyat listesi
echo - Hakedis kayitlari
echo - Servis formlari
echo - Faturalar
echo - Excel ciktilari
echo - Lisans bilgisi
echo - AppData ayarlari
echo.
echo Devam etmek icin EVET yazin.
echo.
set /p confirm=Onay: 

if /I not "%confirm%"=="EVET" (
    echo.
    echo Islem iptal edildi.
    pause
    exit /b
)

echo.
echo Uygulama kapatiliyor...
taskkill /IM ServisHakedis.exe /F >nul 2>&1
taskkill /IM HakedisOtomasyon.exe /F >nul 2>&1
taskkill /IM dotnet.exe /F >nul 2>&1

timeout /t 2 >nul

echo.
echo Kullanici verileri siliniyor...

rmdir /s /q "%USERPROFILE%\Desktop\HAKEDİŞ DATABASE" 2>nul
rmdir /s /q "%USERPROFILE%\Desktop\HAKEDIS DATABASE" 2>nul

rmdir /s /q "%LOCALAPPDATA%\ServisHakedis" 2>nul
rmdir /s /q "%APPDATA%\ServisHakedis" 2>nul

rmdir /s /q "%LOCALAPPDATA%\HakedisOtomasyon" 2>nul
rmdir /s /q "%APPDATA%\HakedisOtomasyon" 2>nul

rmdir /s /q "%LOCALAPPDATA%\SismikHakedis" 2>nul
rmdir /s /q "%APPDATA%\SismikHakedis" 2>nul

echo.
echo Uygulama klasoru siliniyor...

set "APPDIR=%~dp0"

cd /d "%TEMP%"

timeout /t 1 >nul

rmdir /s /q "%APPDIR%" 2>nul

echo.
echo ========================================
echo   KALDIRMA ISLEMI TAMAMLANDI
echo ========================================
echo.
echo Uygulama ve tum veriler silindi.
echo.
pause
exit /b
