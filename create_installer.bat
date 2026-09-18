@echo off
setlocal

echo ======================================================================
echo    GX2 Bridge v0.3.1 - Build ^& Create Installer
echo    Developed by: Ismail Lowkey
echo ======================================================================
echo.

cd /d "%~dp0"

:: 1. Publish WPF Application in Release Mode
echo [1/3] Mem-publish aplikasi dalam mode Release...
dotnet publish "src\NetToGXSim2.Wpf\NetToGXSim2.Wpf.csproj" -c Release -o "publish"
if errorlevel 1 (
    echo.
    echo [ERROR] Gagal mem-publish proyek WPF! Silakan periksa error di atas.
    pause
    exit /b 1
)
echo [OK] Publish selesai di folder publish.
echo.

:: 2. Cari compiler NSIS
echo [2/3] Mencari NSIS Compiler makensis.exe...
set "NSIS_PATH="

if exist "C:\Program Files (x86)\NSIS\makensis.exe" set "NSIS_PATH=C:\Program Files (x86)\NSIS\makensis.exe"
if not defined NSIS_PATH if exist "C:\Program Files\NSIS\makensis.exe" set "NSIS_PATH=C:\Program Files\NSIS\makensis.exe"

if not defined NSIS_PATH (
    where makensis >nul 2>nul
    if not errorlevel 1 set "NSIS_PATH=makensis"
)

if not defined NSIS_PATH goto :NsisNotFound

echo [OK] Menggunakan NSIS di: "%NSIS_PATH%"
echo.

:: 3. Compile installer dengan NSIS
echo [3/3] Meng-compile installer.nsi...
"%NSIS_PATH%" /V2 "installer.nsi"
if errorlevel 1 (
    echo.
    echo [ERROR] Gagal membuat installer dengan NSIS!
    pause
    exit /b 1
)

echo.
echo ======================================================================
echo  [SUCCESS] Installer berhasil dibuat!
echo  File: Setup_NetToGXSim2_v0.3.2.exe
echo ======================================================================
echo.
pause
exit /b 0

:NsisNotFound
echo.
echo [ERROR] NSIS compiler makensis.exe tidak ditemukan!
echo Silakan install NSIS dari https://nsis.sourceforge.io/
echo atau pastikan makensis.exe terpasang di Program Files.
pause
exit /b 1
