@if "%_echo%" neq "on" echo off
setlocal

SET CMDHOME=%~dp0.

:: Clear the 'Platform' env variable for this session, as it's a per-project setting within the build, and
:: misleading value (such as 'MCD' in HP PCs) may lead to build breakage (issue: #69).
set Platform=

:: Restore the Tools directory
call %~dp0init-tools.cmd
if NOT [%ERRORLEVEL%]==[0] exit /b 1

set _toolRuntime=%~dp0Tools
set _dotnet=%_toolRuntime%\dotnetcli\dotnet.exe

SET VERSION_FILE=%CMDHOME%\Build\Version.txt

if EXIST "%VERSION_FILE%" (
    @Echo Using version number from file %VERSION_FILE%
    FOR /F "usebackq tokens=1,2,3,4 delims=." %%i in (`type "%VERSION_FILE%"`) do set PRODUCT_VERSION=%%i.%%j.%%k
) else (
    @Echo ERROR: Unable to read version number from file %VERSION_FILE%
    SET PRODUCT_VERSION=1.0
)
@Echo PRODUCT_VERSION=%PRODUCT_VERSION%

if "%builduri%" == "" set builduri=Build.cmd

set PROJ=%CMDHOME%\Orleans.vNext.sln

@echo ===== Building %PROJ% =====
call %_dotnet% restore "%PROJ%"
call %_dotnet% restore "%CMDHOME%\.nuget\Tools.csproj"

@echo Build Debug ==============================

SET CONFIGURATION=Debug
SET OutDir=%~dp0..\Binaries\%CONFIGURATION%

call %_dotnet% build /m:1 /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %PROJ%

@echo Build Release ============================

SET CONFIGURATION=Release
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

call %_dotnet% build /m:1 /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %PROJ%

REM set STEP=VSIX

REM if "%VSSDK140Install%" == "" (
REM    @echo Visual Studio 2015 SDK not installed - Skipping building VSIX
REM     @GOTO :BuildFinished
REM )

REM @echo Build VSIX ============================

REM set PROJ=%CMDHOME%\OrleansVSTools\OrleansVSTools.sln
REM SET OutDir=%OutDir%\VSIX
REM "%MSBUILDEXE%" /nr:False /m /p:Configuration=%CONFIGURATION% "%PROJ%"
REM @if ERRORLEVEL 1 GOTO :ErrorStop
REM @echo BUILD ok for VSIX package for %PROJ%

:BuildFinished
@echo ===== Build succeeded for %PROJ% =====
@GOTO :EOF

:ErrorStop
set RC=%ERRORLEVEL%
if "%STEP%" == "" set STEP=%CONFIGURATION%
@echo ===== Build FAILED for %PROJ% -- %STEP% with error %RC% - CANNOT CONTINUE =====
exit /B %RC%
:EOF
