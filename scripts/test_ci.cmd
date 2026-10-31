@echo off
setlocal
cd /d "%~dp0\.."

echo Building echo canceller tests...

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: Windows .NET Framework C# compiler not found.
    exit /b 1
)

if not exist "obj" mkdir "obj"
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\EchoCancellerTests.exe" "tests\EchoCancellerTests.cs" "src\EchoCanceller.cs"
if errorlevel 1 exit /b 1

"obj\EchoCancellerTests.exe"
if errorlevel 1 exit /b 1

echo.
echo Building chat tests...
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\ChatTests.exe" /main:Maela.Tests.ChatTests /reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll "src\*.cs" "tests\ChatTests.cs"
if errorlevel 1 exit /b 1

"obj\ChatTests.exe"
if errorlevel 1 exit /b 1
exit /b 0
