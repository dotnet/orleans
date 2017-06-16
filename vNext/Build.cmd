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

SET SOLUTION=%CMDHOME%\Orleans.vNext.sln

:: Set DateTime suffix for debug builds
for /f %%i in ('powershell -NoProfile -ExecutionPolicy ByPass Get-Date -format "{yyyyMMddHHmm}"') do set DATE_SUFFIX=%%i

if "%1" == "Pack" GOTO :Package

@echo ===== Building %SOLUTION% =====
call %_dotnet% restore "%CMDHOME%\Build\Tools.csproj" --packages %TOOLS_PACKAGES_PATH%

@echo Build Debug ==============================

SET CURRENT_CONFIGURATION=Debug

call %_dotnet% restore /p:Configuration=%CURRENT_CONFIGURATION%;IncludeFSharp=true "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo RESTORE ok for %CURRENT_CONFIGURATION% %SOLUTION%

call %_dotnet% build %BUILD_FLAGS% /p:Configuration=%CURRENT_CONFIGURATION%;IncludeFSharp=true "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CURRENT_CONFIGURATION% %SOLUTION%

call %_dotnet% pack --no-build %BUILD_FLAGS% /p:Configuration=%CURRENT_CONFIGURATION%;IncludeFSharp=true;VersionDateSuffix=%DATE_SUFFIX% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo PACKAGE ok for %CURRENT_CONFIGURATION% %SOLUTION%

@echo Build Release ============================

SET CURRENT_CONFIGURATION=Release

call %_dotnet% restore /p:Configuration=%CURRENT_CONFIGURATION%;IncludeFSharp=true "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo RESTORE ok for %CURRENT_CONFIGURATION% %SOLUTION%

call %_dotnet% build %BUILD_FLAGS% /p:Configuration=%CURRENT_CONFIGURATION%;IncludeFSharp=true "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop                                    
@echo BUILD ok for %CURRENT_CONFIGURATION% %SOLUTION%

call %_dotnet% pack --no-build %BUILD_FLAGS% /p:Configuration=%CURRENT_CONFIGURATION%;IncludeFSharp=true "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop                                    
@echo PACKAGE ok for %CURRENT_CONFIGURATION% %SOLUTION%

goto :BuildFinished

:Package
@echo Package Debug ============================

SET CURRENT_CONFIGURATION=Debug

call %_dotnet% pack --no-build %BUILD_FLAGS% /p:Configuration=%CURRENT_CONFIGURATION%;IncludeFSharp=true;VersionDateSuffix=%DATE_SUFFIX% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo PACKAGE ok for %CURRENT_CONFIGURATION% %SOLUTION%

@echo Package Release ============================

SET CURRENT_CONFIGURATION=Release

call %_dotnet% pack --no-build %BUILD_FLAGS% /p:Configuration=%CURRENT_CONFIGURATION%;IncludeFSharp=true "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop                                    
@echo PACKAGE ok for %CURRENT_CONFIGURATION% %SOLUTION%

:BuildFinished
@echo ===== Build succeeded for %SOLUTION% =====
@GOTO :EOF

:ErrorStop
set RC=%ERRORLEVEL%
if "%STEP%" == "" set STEP=%CURRENT_CONFIGURATION%
@echo ===== Build FAILED for %SOLUTION% -- %STEP% with error %RC% - CANNOT CONTINUE =====
exit /B %RC%
:EOF
