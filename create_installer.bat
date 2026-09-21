@echo off
setlocal

echo ======================================================================
echo    NetToGXSim2 v0.5.4 - Build ^& Create Installer
echo    Developed by: Ismail Lowkey
echo ======================================================================
echo.

cd /d "%~dp0"

:: 1. Publish WPF Application in Release Mode
echo [1/3] Publishing WPF application in Release mode...
dotnet publish "src\NetToGXSim2.Wpf\NetToGXSim2.Wpf.csproj" -c Release -o "publish"
if errorlevel 1 (
    echo.
    echo [ERROR] Failed to publish WPF project! Please check the build errors above.
    pause
    exit /b 1
)
echo [OK] Publish completed to publish folder.
echo.

:: 2. Find NSIS compiler
echo [2/3] Searching for NSIS Compiler makensis.exe...
set "NSIS_PATH="

if exist "C:\Program Files (x86)\NSIS\makensis.exe" set "NSIS_PATH=C:\Program Files (x86)\NSIS\makensis.exe"
if not defined NSIS_PATH if exist "C:\Program Files\NSIS\makensis.exe" set "NSIS_PATH=C:\Program Files\NSIS\makensis.exe"

if not defined NSIS_PATH (
    where makensis >nul 2>nul
    if not errorlevel 1 set "NSIS_PATH=makensis"
)

if not defined NSIS_PATH goto :NsisNotFound

echo [OK] Found NSIS compiler at: "%NSIS_PATH%"
echo.

:: 3. Compile installer with NSIS
echo [3/3] Compiling installer.nsi...
"%NSIS_PATH%" /V2 "installer.nsi"
if errorlevel 1 (
    echo.
    echo [ERROR] Failed to build installer with NSIS!
    pause
    exit /b 1
)

echo.
echo ======================================================================
echo  [SUCCESS] Installer built successfully!
echo  File: Setup_NetToGXSim2_v0.5.4.exe
echo ======================================================================
echo.
pause
exit /b 0

:NsisNotFound
echo.
echo [ERROR] NSIS compiler makensis.exe was not found!
echo Please install NSIS from https://nsis.sourceforge.io/
echo or ensure makensis.exe is in your system PATH or Program Files.
pause
exit /b 1
