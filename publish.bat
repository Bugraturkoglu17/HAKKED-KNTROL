@echo off
chcp 65001 > nul
echo ================================================
echo  Servis Hakediş - Yayınlama (Publish)
echo ================================================
echo.

set "DOTNET=%USERPROFILE%\dotnet\dotnet.exe"
if not exist "%DOTNET%" set "DOTNET=dotnet"

set "PROJECT=C:\Users\bturkoglu\Desktop\HakedisOtomasyon\HakedisOtomasyon\src\HakedisOtomasyon.Web"
set "OUTPUT=C:\Users\bturkoglu\Desktop\HakedisOtomasyon\publish\ServisHakedis"

echo Derleniyor ve yayınlanıyor...
echo Çıktı klasörü: %OUTPUT%
echo.

"%DOTNET%" publish "%PROJECT%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -o "%OUTPUT%" ^
    /p:PublishReadyToRun=true ^
    /p:DebugType=None ^
    /p:DebugSymbols=false

if %ERRORLEVEL% neq 0 (
    echo.
    echo HATA: Yayınlama başarısız oldu!
    pause
    exit /b 1
)

echo.
echo ================================================
echo  Başarıyla yayınlandı!
echo  Klasör: %OUTPUT%
echo.
echo  Çalıştırmak için:
echo    %OUTPUT%\ServisHakedis.exe
echo  veya
echo    İcmal Kontrol.py  (Python ile)
echo ================================================
echo.
pause
