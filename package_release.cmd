@echo off
setlocal
cd /d "%~dp0"

call scripts\build_ci.cmd
if errorlevel 1 exit /b 1

set /p VERSION=<VERSION
if not exist "release" mkdir "release"
if exist "release\Maela-v%VERSION%-windows.zip" del /q "release\Maela-v%VERSION%-windows.zip"

powershell -NoProfile -ExecutionPolicy Bypass -Command "Compress-Archive -Path 'dist\Maela.exe','dist\README.md','dist\LICENSE','dist\VERSION','dist\MaelaLogo.png' -DestinationPath 'release\Maela-v%VERSION%-windows.zip' -Force"
if errorlevel 1 exit /b 1

echo.
echo Release package created:
echo release\Maela-v%VERSION%-windows.zip
exit /b 0
