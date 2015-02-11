@REM NOTE: This script must be run from a Visual Studio command prompt window

@ECHO on

SET CMDHOME=%~dp0.
if "%FrameworkDir%" == "" set FrameworkDir=%WINDIR%\Microsoft.NET\Framework
if "%FrameworkVersion%" == "" set FrameworkVersion=v4.0.30319

SET MSTESTEXEDIR=%VS120COMNTOOLS%..\IDE
SET MSTESTEXE=%MSTESTEXEDIR%\MSTest.exe

SET CONFIGURATION=Release
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

cd "%CMDHOME%"

"%MSTESTEXE%"  /testcontainer:%OutDir%\TesterInternal.dll /category:"BVT|Nightly"

"%MSTESTEXE%"  /testcontainer:%OutDir%\Tester.dll /category:"BVT|Nightly"