@if not defined _echo @echo off
setlocal

if [%1]==[]                GOTO NETFX
if [%1]==[netfx]           GOTO NETFX
if [%1]==[netstandard-win] GOTO VNEXT
if [%1]==[netstandard]     GOTO VNEXT
if [%1]==[all]             GOTO ALL

:NETFX
cmd /c "%~dp0src\Test.cmd"
set exitcode=%errorlevel%
GOTO END

:VNEXT
cmd /c "%~dp0vNext\Test.cmd"
set exitcode=%errorlevel%
GOTO END

:ALL
cmd /c "%~dp0src\Test.cmd"
set exitcode=%errorlevel%
cmd /c "%~dp0vNext\Test.cmd"
set /a exitcode=%errorlevel%+%exitcode%

:END
endlocal&set exitcode=%exitcode%
exit /B %exitcode%