@echo off
setlocal
cd /d "%~dp0"

call scripts\build_ci.cmd
if errorlevel 1 (
    echo.
    echo Build failed.
    pause
    exit /b 1
)

echo.
echo Build succeeded: %CD%\dist\Maela.exe
echo.
echo Launch dist\Maela.exe to run maela.
pause
