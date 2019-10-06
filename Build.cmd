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

SET SOLUTION=%CMDHOME%\Orleans.sln

:: Set DateTime suffix for debug builds
if "%BuildConfiguration%" == "Debug" for /f %%j in ('powershell -NoProfile -ExecutionPolicy ByPass Get-Date -format "{yyyyMMddHHmm}"') do set DATE_SUFFIX=%%j
if "%BuildConfiguration%" == "Debug" (
    SET VersionDateSuffix=%DATE_SUFFIX%
    SET AdditionalConfigurationProperties=;VersionDateSuffix=%VersionDateSuffix%
)

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

SET NUGET_REMOTE_PATH=https://dist.nuget.org/win-x86-commandline/latest/nuget.exe
SET NUGET_LOCAL_DIR=%CMDHOME%\.nuget
SET NUGET_LOCAL_PATH=%NUGET_LOCAL_DIR%\nuget.exe
if "%Configuration%" == "" set Configuration=Debug 

if NOT exist "%NUGET_LOCAL_PATH%" (
  if NOT exist "%NUGET_LOCAL_DIR%" mkdir %NUGET_LOCAL_DIR%
  echo Downloading nuget.exe from %NUGET_REMOTE_PATH%.
  powershell -NoProfile -ExecutionPolicy unrestricted -Command "[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; $retryCount = 0; $success = $false; do { try { (New-Object Net.WebClient).DownloadFile('%NUGET_REMOTE_PATH%', '%NUGET_LOCAL_PATH%'); $success = $true; } catch { if ($retryCount -ge 6) { throw; } else { $retryCount++; Start-Sleep -Seconds (5 * $retryCount); } } } while ($success -eq $false);"
  if NOT exist "%NUGET_LOCAL_PATH%" (
    echo ERROR: Could not download nuget.exe correctly, falling back to environment variables.
    goto :ErrorStop
  )
)

if "%VersionPrefix%" == "" SET VersionPrefix=3.0.0
if "%Configuration%" == "Debug" (
    if "%VersionSuffix%" == "" (
        SET VersionSuffix=dev
    )
)
set Version=%VersionPrefix%
if NOT "%VersionSuffix%" == "" set Version=%Version%-%VersionSuffix%

call %NUGET_LOCAL_PATH% pack -Version %Version% -OutputDirectory %CMDHOME%\Artifacts\%Configuration% %CMDHOME%\src\AdoNet\Orleans.Extensions.AdoNet.Scripts\Orleans.Extensions.AdoNet.Scripts.nuspec
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
