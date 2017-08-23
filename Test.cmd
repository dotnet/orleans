@if not defined _echo @echo off
setlocal

if [%1]==[]                GOTO CURRENT
if [%1]==[current]         GOTO CURRENT
if [%1]==[netstandard-win] GOTO CURRENT
if [%1]==[netstandard]     GOTO CURRENT
if [%1]==[netfx]           GOTO LEGACY
if [%1]==[legacy]          GOTO LEGACY
if [%1]==[all]             GOTO ALL

:CURRENT
set BuildFlavor=
cmd /c "%~dp0Test-Core.cmd"
set exitcode=%errorlevel%
GOTO END

:LEGACY
set BuildFlavor=Legacy
cmd /c "%~dp0Test-Core.cmd"
set exitcode=%errorlevel%
GOTO END

:ALL
set BuildFlavor=
cmd /c "%~dp0Test-Core.cmd"
set exitcode=%errorlevel%

set BuildFlavor=Legacy
cmd /c "%~dp0Test-Core.cmd"
set /a exitcode=%errorlevel%+%exitcode%

:END
endlocal&set exitcode=%exitcode%
exit /B %exitcode%
