@echo off
setlocal
cd /d "%~dp0\.."

rem Builds and runs every test suite. Tests that touch the network engine are compiled
rem against the real Opus codec (fetched by scripts\fetch_deps.ps1), exactly like Maela.exe.

set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
    echo ERROR: Windows .NET Framework C# compiler not found.
    exit /b 1
)

if not exist "obj" mkdir "obj"
rem Fetch and verify build-time dependencies (see deps\*.lock).
powershell -NoProfile -ExecutionPolicy Bypass -File "scripts\fetch_deps.ps1"
if errorlevel 1 (
    echo ERROR: dependency download or verification failed.
    exit /b 1
)
set "REFS=/reference:System.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Xml.dll /reference:System.Security.dll"
set "ENGINE=/define:PARITY /nowarn:0162,0168,0219,0414,0649,0675,1591,1573 "src\*.cs" "tests\TestSupport.cs" /recurse:"obj\deps\concentus\CSharp\Concentus\*.cs""

echo === Crypto ===
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\CryptoTests.exe" /reference:System.Security.dll "tests\CryptoTests.cs" "src\Crypto.cs"
if errorlevel 1 exit /b 1
"obj\CryptoTests.exe"
if errorlevel 1 exit /b 1

echo.
echo === Echo canceller ===
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\EchoCancellerTests.exe" "tests\EchoCancellerTests.cs" "src\EchoCanceller.cs"
if errorlevel 1 exit /b 1
"obj\EchoCancellerTests.exe"
if errorlevel 1 exit /b 1

echo.
echo === Noise suppressor ===
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\NoiseSuppressorTests.exe" "tests\NoiseSuppressorTests.cs" "src\NoiseSuppressor.cs" "src\EchoCanceller.cs"
if errorlevel 1 exit /b 1
"obj\NoiseSuppressorTests.exe"
if errorlevel 1 exit /b 1

echo.
echo === Automatic mic level ===
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\AutoGainTests.exe" "tests\AutoGainTests.cs" "src\AutoGain.cs"
if errorlevel 1 exit /b 1
"obj\AutoGainTests.exe"
if errorlevel 1 exit /b 1

echo.
echo === Sound effects ===
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\SoundTests.exe" "tests\SoundTests.cs" "src\SoundFx.cs"
if errorlevel 1 exit /b 1
"obj\SoundTests.exe"
if errorlevel 1 exit /b 1

echo.
echo === Security ===
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\SecurityTests.exe" /main:Maela.Tests.SecurityTests %REFS% %ENGINE% "tests\SecurityTests.cs"
if errorlevel 1 exit /b 1
"obj\SecurityTests.exe"
if errorlevel 1 exit /b 1

echo.
echo === Voice (Opus) ===
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\VoiceTests.exe" /main:Maela.Tests.VoiceTests %REFS% %ENGINE% "tests\VoiceTests.cs"
if errorlevel 1 exit /b 1
"obj\VoiceTests.exe"
if errorlevel 1 exit /b 1

echo.
echo === Chat ===
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\ChatTests.exe" /main:Maela.Tests.ChatTests %REFS% %ENGINE% "tests\ChatTests.cs"
if errorlevel 1 exit /b 1
"obj\ChatTests.exe"
if errorlevel 1 exit /b 1

echo.
echo === Downloads (official Twemoji source, verified) ===
"%CSC%" /nologo /target:exe /optimize+ /out:"obj\AssetTests.exe" /main:Maela.Tests.AssetTests %REFS% %ENGINE% "tests\AssetTests.cs"
if errorlevel 1 exit /b 1
"obj\AssetTests.exe"
if errorlevel 1 exit /b 1

echo.
echo All test suites passed.
exit /b 0
