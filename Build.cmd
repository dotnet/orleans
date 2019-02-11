@if not defined _echo @echo off
setlocal enabledelayedexpansion

SET CMDHOME=%~dp0.
if "%BUILD_FLAGS%"=="" SET BUILD_FLAGS=/m /v:m
if not defined BuildConfiguration SET BuildConfiguration=Debug

:: Clear the 'Platform' env variable for this session, as it's a per-project setting within the build, and
:: misleading value (such as 'MCD' in HP PCs) may lead to build breakage (issue: #69).
set Platform=

:: Disable multilevel lookup https://github.com/dotnet/core-setup/blob/master/Documentation/design-docs/multilevel-sharedfx-lookup.md
set DOTNET_MULTILEVEL_LOOKUP=0 

call Ensure-DotNetSdk.cmd
SET DotNetHost=%_dotnet%

SET SOLUTION=%CMDHOME%\Orleans.sln

:: Set DateTime suffix for debug builds
if "%BuildConfiguration%" == "Debug" for /f %%j in ('powershell -NoProfile -ExecutionPolicy ByPass Get-Date -format "{yyyyMMddHHmm}"') do set DATE_SUFFIX=%%j
if "%BuildConfiguration%" == "Debug" SET AdditionalConfigurationProperties=;VersionDateSuffix=%DATE_SUFFIX%

if "%1" == "Pack" GOTO :Package

@echo ===== Building %SOLUTION% =====

@echo Build %BuildConfiguration% ==============================
SET STEP=Restore %BuildConfiguration%

call %_dotnet% restore %BUILD_FLAGS% /bl:%BuildConfiguration%-Restore.binlog /p:Configuration=%BuildConfiguration%%AdditionalConfigurationProperties% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo RESTORE ok for %BuildConfiguration% %SOLUTION%

SET STEP=Build %BuildConfiguration%
call %_dotnet% build --no-restore %BUILD_FLAGS% /bl:%BuildConfiguration%-Build.binlog /p:Configuration=%BuildConfiguration%%AdditionalConfigurationProperties% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %BuildConfiguration% %SOLUTION%


:Package
@echo Package BuildConfiguration ============================
SET STEP=Pack %BuildConfiguration%

call %_dotnet% pack --no-build --no-restore %BUILD_FLAGS% /bl:%BuildConfiguration%-Pack.binlog /p:Configuration=%BuildConfiguration%%AdditionalConfigurationProperties% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo PACKAGE ok for %BuildConfiguration% %SOLUTION%


:BuildFinished
@echo ===== Build succeeded for %SOLUTION% =====
@GOTO :EOF

:ErrorStop
set RC=%ERRORLEVEL%
if "%STEP%" == "" set STEP=%BuildConfiguration%
@echo ===== Build FAILED for %SOLUTION% -- %STEP% with error %RC% - CANNOT CONTINUE =====
exit /B %RC%
:EOF
