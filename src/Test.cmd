@REM NOTE: This script must be run from a Visual Studio command prompt window

@ECHO on

SET CMDHOME=%~dp0.
SET CONFIGURATION=Debug
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

cd %CMDHOME%

mstest.exe /testcontainer:%OutDir%\Tester.dll