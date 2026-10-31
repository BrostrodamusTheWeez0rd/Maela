@echo off
setlocal
cd /d "%~dp0\.."

echo Building Maela.exe...

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"

if not exist "%CSC%" (
    echo ERROR: Windows .NET Framework C# compiler not found.
    exit /b 1
)
if not exist "src\Maela.cs" (
    echo ERROR: src\Maela.cs not found.
    exit /b 1
)
if not exist "assets\Maela.ico" (
    echo ERROR: assets\Maela.ico not found.
    exit /b 1
)
if not exist "assets\MaelaMark.png" (
    echo ERROR: assets\MaelaMark.png not found.
    exit /b 1
)

if not exist "dist" mkdir "dist"

"%CSC%" /nologo /target:winexe /optimize+ /out:"dist\Maela.exe" /win32icon:"assets\Maela.ico" /resource:"assets\MaelaMark.png",MaelaMark.png /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll "src\Maela.cs"
if errorlevel 1 exit /b 1

copy /y "README.md" "dist\README.md" >nul
copy /y "LICENSE" "dist\LICENSE" >nul
copy /y "VERSION" "dist\VERSION" >nul
copy /y "assets\MaelaLogo.png" "dist\MaelaLogo.png" >nul

echo Built dist\Maela.exe
exit /b 0
