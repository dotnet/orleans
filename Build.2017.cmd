@if not defined _echo @echo off
setlocal

SET CMDHOME=%~dp0.
SET BUILD_FLAGS=/m /v:m /fl /flp:logfile=build.log;verbosity=detailed

:: Clear the 'Platform' env variable for this session, as it's a per-project setting within the build, and
:: misleading value (such as 'MCD' in HP PCs) may lead to build breakage (issue: #69).
set Platform=

:: Restore the Tools directory
call %~dp0init-tools.cmd
if NOT [%ERRORLEVEL%]==[0] exit /b 1

set _toolRuntime=%~dp0Tools
set _dotnet=%_toolRuntime%\dotnetcli\dotnet.exe

SET TOOLS_PACKAGES_PATH=%CMDHOME%\packages

SET SOLUTION=%CMDHOME%\Orleans.2017.sln

if "%1" == "Pack" GOTO :Package

@echo ===== Building %SOLUTION% =====
call %_dotnet% restore "%CMDHOME%\Build\Tools.csproj" --packages %TOOLS_PACKAGES_PATH%

:: Restore packages for the solution
call %_dotnet% restore /p:IncludeFSharp=true "%SOLUTION%"

@echo Build Debug ==============================

SET Configuration=Debug

call %_dotnet% build %BUILD_FLAGS% /p:Configuration=%CONFIGURATION%;IncludeFSharp=true "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %SOLUTION%

@echo Build Release ============================

SET CONFIGURATION=Release

call %_dotnet% build %BUILD_FLAGS% /p:Configuration=%CONFIGURATION%;IncludeFSharp=true "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop                                    
@echo BUILD ok for %CONFIGURATION% %SOLUTION%

:Package
@echo Package Debug ============================
:: Set DateTime suffix for debug builds
for /f %%i in ('powershell -NoProfile -ExecutionPolicy ByPass Get-Date -format "{yyyyMMddHHmm}"') do set DATE_SUFFIX=%%i

SET Configuration=Debug

call %_dotnet% pack --no-build %BUILD_FLAGS% /p:Configuration=%CONFIGURATION%;IncludeFSharp=true;VersionDateSuffix=%DATE_SUFFIX% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo PACKAGE ok for %CONFIGURATION% %SOLUTION%

@echo Package Release ============================

SET CONFIGURATION=Release

call %_dotnet% pack %BUILD_FLAGS% /p:Configuration=%CONFIGURATION%;IncludeFSharp=true "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop                                    
@echo PACKAGE ok for %CONFIGURATION% %SOLUTION%


:BuildFinished
@echo ===== Build succeeded for %SOLUTION% =====
@GOTO :EOF

:ErrorStop
set RC=%ERRORLEVEL%
if "%STEP%" == "" set STEP=%CONFIGURATION%
@echo ===== Build FAILED for %SOLUTION% -- %STEP% with error %RC% - CANNOT CONTINUE =====
exit /B %RC%
:EOF
