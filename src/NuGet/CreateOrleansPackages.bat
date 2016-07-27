@Echo OFF
@setlocal EnableExtensions EnableDelayedExpansion

IF %1.==. GOTO Usage

set NUGET_EXE=%~dp0..\.nuget\nuget.exe

set BASE_PATH=%1
set VERSION=%2
set SRC_DIR=%3
set PRERELEASE_BUILD=%4
IF %2 == "" set VERSION=%~dp0..\Build\Version.txt

@echo CreateOrleansNugetPackages running in directory = %1
@cd
@echo CreateOrleansNugetPackages version file = %VERSION% from base dir = %BASE_PATH% using nuget location = %NUGET_EXE%

if "%BASE_PATH%" == "." (
	if EXIST "Release" (
		set BASE_PATH=Release
	) else if EXIST "Debug" (
		set BASE_PATH=Debug
	)
)
@echo Using binary drop location directory %BASE_PATH%

if EXIST "%VERSION%" (
      @Echo Using version number from file %VERSION%
      FOR /F "usebackq tokens=1,2,3,4 delims=." %%i in (`type "%VERSION%"`) do (

	   set VERSION=%%i.%%j.%%k
	   set VERSION_BETA=%%l
	  )
	  
) else (
    @Echo ERROR: Unable to read version number from file %VERSION%
    GOTO Usage
)

if not "%VERSION_BETA%" == "" (
    @echo VERSION_BETA=!VERSION_BETA!
    set VERSION=%VERSION%-!VERSION_BETA!
)

if "%PRERELEASE_BUILD%" == "true" (
    if "%VERSION_BETA%" == "" (set VERSION_TYPE=Dev) else (set VERSION_TYPE=)
    for /f %%i in ('powershell -NoProfile -ExecutionPolicy ByPass Get-Date -format "{yyyyMMddHHmm}"') do set VERSION_TIMESTAMP=%%i
    @echo VERSION_TYPE = !VERSION_TYPE!
    @echo VERSION_TIMESTAMP = !VERSION_TIMESTAMP!
    set VERSION=%VERSION%-!VERSION_TYPE!!VERSION_TIMESTAMP!
)

@echo VERSION=!VERSION!

@echo CreateOrleansNugetPackages: Version = !VERSION! -- Drop location = %BASE_PATH% -- SRC_DIR=%SRC_DIR%

@set NUGET_PACK_OPTS= -Version !VERSION!
@set NUGET_PACK_OPTS=%NUGET_PACK_OPTS% -NoPackageAnalysis
@set NUGET_PACK_OPTS=%NUGET_PACK_OPTS% -BasePath "%BASE_PATH%" -Properties SRC_DIR=%SRC_DIR%
REM @set NUGET_PACK_OPTS=%NUGET_PACK_OPTS% -Verbosity detailed

FOR %%G IN ("%~dp0*.nuspec") DO (
  "%NUGET_EXE%" pack "%%G" %NUGET_PACK_OPTS% -Symbols
  if ERRORLEVEL 1 EXIT /B 1
)

FOR %%G IN ("%~dp0*.nuspec-NoSymbols") DO (
  REM %%~dpnG gets the full filename path but without the extension
  move "%%G" "%%~dpnG.nuspec"
  "%NUGET_EXE%" pack "%%~dpnG.nuspec" %NUGET_PACK_OPTS%
  if ERRORLEVEL 1 EXIT /B 1
  move "%%~dpnG.nuspec" "%%G"
)

GOTO EOF

:Usage
@ECHO Usage:
@ECHO    CreateOrleansPackages ^<Path to Orleans SDK folder^> ^<VersionFile^>
EXIT /B -1

:EOF
