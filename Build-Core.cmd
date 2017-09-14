@if not defined _echo @echo off
setlocal

SET CMDHOME=%~dp0.
if "%BUILD_FLAGS%"=="" SET BUILD_FLAGS=/m:1 /v:m

:: Clear the 'Platform' env variable for this session, as it's a per-project setting within the build, and
:: misleading value (such as 'MCD' in HP PCs) may lead to build breakage (issue: #69).
set Platform=

for /f "tokens=*" %%i in ('where dotnet.exe') do set INSTALLED_DOTNET_EXE=%%i

if not exist "%INSTALLED_DOTNET_EXE%" goto :install-dotnet

echo Found dotnet.exe at: "%INSTALLED_DOTNET_EXE%"

for /f "tokens=*" %%i in ('"%INSTALLED_DOTNET_EXE%" --version') do set INSTALLED_DOTNET_VERSION=%%i

if [%INSTALLED_DOTNET_VERSION%]==[] (
  echo Cannot determine version of installed .Net Core
  goto :install-dotnet
)

echo .Net Core version found: %INSTALLED_DOTNET_VERSION%

set /p REQUIRED_DOTNET_VERSION=< "%~dp0DotnetCLIVersion.txt"

echo .Net Core version required: %REQUIRED_DOTNET_VERSION%

if [%REQUIRED_DOTNET_VERSION%] neq [%INSTALLED_DOTNET_VERSION%] (
  echo .Net Core version mismatch, installing.
  goto :install-dotnet
) else (
  echo .Net Core version is matching, using the installed version.

  set _dotnet="%INSTALLED_DOTNET_EXE%"

  goto :dotnet-installed
)

:install-dotnet

:: Restore the Tools directory
call %~dp0init-tools.cmd
if NOT [%ERRORLEVEL%]==[0] exit /b 1

set _toolRuntime=%~dp0Tools
set _dotnet=%_toolRuntime%\dotnetcli\dotnet.exe

SET PATH=%_toolRuntime%\dotnetcli;%PATH%

:dotnet-installed

SET TOOLS_PACKAGES_PATH=%CMDHOME%\packages

SET SOLUTION=%CMDHOME%\Orleans.sln

SET LOGFILENAME_PREFIX=%BuildFlavor%

:: For log filename prefix if we're building default which is Current set it properly
if "%LOGFILENAME_PREFIX%"=="" SET LOGFILENAME_PREFIX=Current

:: Set DateTime suffix for debug builds
for /f %%i in ('powershell -NoProfile -ExecutionPolicy ByPass Get-Date -format "{yyyyMMddHHmm}"') do set DATE_SUFFIX=%%i

if "%1" == "Pack" GOTO :Package

@echo ===== Building %SOLUTION% =====
call %_dotnet% restore "%CMDHOME%\Build\Tools.csproj" --packages %TOOLS_PACKAGES_PATH%

@echo Build Debug ==============================

SET CURRENT_CONFIGURATION=Debug

call %_dotnet% restore %BUILD_FLAGS% /bl:%LOGFILENAME_PREFIX%-%CURRENT_CONFIGURATION%-Restore.binlog /p:Configuration=%CURRENT_CONFIGURATION% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo RESTORE ok for %CURRENT_CONFIGURATION% %SOLUTION%

call %_dotnet% build %BUILD_FLAGS% /bl:%LOGFILENAME_PREFIX%-%CURRENT_CONFIGURATION%-Build.binlog /p:Configuration=%CURRENT_CONFIGURATION% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CURRENT_CONFIGURATION% %SOLUTION%

call %_dotnet% pack --no-build %BUILD_FLAGS% /bl:%LOGFILENAME_PREFIX%-%CURRENT_CONFIGURATION%-Pack.binlog /p:Configuration=%CURRENT_CONFIGURATION%;VersionDateSuffix=%DATE_SUFFIX% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo PACKAGE ok for %CURRENT_CONFIGURATION% %SOLUTION%

@echo Build Release ============================

SET CURRENT_CONFIGURATION=Release

call %_dotnet% restore %BUILD_FLAGS% /bl:%LOGFILENAME_PREFIX%-%CURRENT_CONFIGURATION%-Restore.binlog /p:Configuration=%CURRENT_CONFIGURATION% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo RESTORE ok for %CURRENT_CONFIGURATION% %SOLUTION%

call %_dotnet% build %BUILD_FLAGS% /bl:%LOGFILENAME_PREFIX%-%CURRENT_CONFIGURATION%-Build.binlog /p:Configuration=%CURRENT_CONFIGURATION% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop                                    
@echo BUILD ok for %CURRENT_CONFIGURATION% %SOLUTION%

call %_dotnet% pack --no-build %BUILD_FLAGS% /bl:%LOGFILENAME_PREFIX%-%CURRENT_CONFIGURATION%-Pack.binlog /p:Configuration=%CURRENT_CONFIGURATION% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop                                    
@echo PACKAGE ok for %CURRENT_CONFIGURATION% %SOLUTION%

goto :BuildFinished

:Package
@echo Package Debug ============================

SET CURRENT_CONFIGURATION=Debug

call %_dotnet% pack --no-build %BUILD_FLAGS% /bl:%LOGFILENAME_PREFIX%-%CURRENT_CONFIGURATION%-Pack.binlog /p:Configuration=%CURRENT_CONFIGURATION%;VersionDateSuffix=%DATE_SUFFIX% "%SOLUTION%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo PACKAGE ok for %CURRENT_CONFIGURATION% %SOLUTION%

@echo Package Release ============================

SET CURRENT_CONFIGURATION=Release

call %_dotnet% pack --no-build %BUILD_FLAGS% /bl:%LOGFILENAME_PREFIX%-%CURRENT_CONFIGURATION%-Pack.binlog /p:Configuration=%CURRENT_CONFIGURATION% "%SOLUTION%"
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
