@if "%_echo%" neq "on" echo off
setlocal

SET CMDHOME=%~dp0.
SET BUILD_FLAGS=/m:1 /v:m /fl /flp:logfile=build.log;verbosity=detailed

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

SET BINARIES_PATH=%CMDHOME%\..\Binaries
SET TOOLS_PACKAGES_PATH=%CMDHOME%\packages

set SOLUTION=%CMDHOME%\Orleans.vNext.sln

@echo ===== Building %SOLUTION% =====
call %_dotnet% restore "%CMDHOME%\Build\Tools.csproj" --packages %TOOLS_PACKAGES_PATH%

:: Restore the code generator related packages individually
call %_dotnet% restore "%CMDHOME%\Orleans.PlatformServices\Orleans.PlatformServices.csproj"
call %_dotnet% restore "%CMDHOME%\Orleans\Orleans.csproj"
call %_dotnet% restore "%CMDHOME%\OrleansCodeGenerator\OrleansCodeGenerator.csproj"
call %_dotnet% restore "%CMDHOME%\ClientGenerator\ClientGenerator.csproj"

:: Restore packages for the solution
call %_dotnet% restore "%SOLUTION%"

@echo Build Debug ==============================

SET Configuration=Debug
SET OutputPath=%BINARIES_PATH%\%CONFIGURATION%

call %_dotnet% build %BUILD_FLAGS% /p:ArtifactDirectory=%OutputPath%\;Configuration=%CONFIGURATION% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %SOLUTION%

call "%CMDHOME%\NuGet\CreateOrleansPackages.bat" %_dotnet% %OutputPath% %VERSION_FILE% %CMDHOME%\ true
@if ERRORLEVEL 1 GOTO :ErrorStop

@echo Build Release ============================

SET CONFIGURATION=Release
SET OutputPath=%BINARIES_PATH%\%CONFIGURATION%

call %_dotnet% build %BUILD_FLAGS% /p:ArtifactDirectory=%OutputPath%\;Configuration=%CONFIGURATION% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop                                    
@echo BUILD ok for %CONFIGURATION% %SOLUTION%

call "%CMDHOME%\NuGet\CreateOrleansPackages.bat" %_dotnet% %OutputPath% %VERSION_FILE% %CMDHOME%\ true
@if ERRORLEVEL 1 GOTO :ErrorStop

REM set STEP=VSIX

REM if "%VSSDK140Install%" == "" (
REM    @echo Visual Studio 2015 SDK not installed - Skipping building VSIX
REM     @GOTO :BuildFinished
REM )

REM @echo Build VSIX ============================

REM set PROJ=%CMDHOME%\OrleansVSTools\OrleansVSTools.sln
REM SET OutputPath=%OutputPath%\VSIX
REM "%MSBUILDEXE%" /nr:False /m /p:Configuration=%CONFIGURATION% "%SOLUTION%"
REM @if ERRORLEVEL 1 GOTO :ErrorStop
REM @echo BUILD ok for VSIX package for %SOLUTION%

:BuildFinished
@echo ===== Build succeeded for %SOLUTION% =====
@GOTO :EOF

:ErrorStop
set RC=%ERRORLEVEL%
if "%STEP%" == "" set STEP=%CONFIGURATION%
@echo ===== Build FAILED for %SOLUTION% -- %STEP% with error %RC% - CANNOT CONTINUE =====
exit /B %RC%
:EOF
