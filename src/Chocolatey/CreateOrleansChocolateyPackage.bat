@Echo OFF
@setlocal

IF %1.==. GOTO Usage

set CPACK_EXE=cpack.exe

set BASE_PATH=%1
set VERSION=%2
IF %2 == "" set VERSION=%~dp0..\Build\Version.txt

@echo CreateOrleansChocolateyPackage running in directory =
@cd
@echo CreateOrleansChocolateyPackage version file = %VERSION% from base dir = %BASE_DIR% using cpack location = %CPACK_EXE%

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
    FOR /F "usebackq tokens=1,2,3,4 delims=." %%i in (`type %VERSION%`) do set VERSION=%%i.%%j.%%k
) else (
    @Echo ERROR: Unable to read version number from file %VERSION%
    GOTO Usage
)

@echo CreateOrleansChocolateyPackage: Version = %VERSION% -- Drop location = %BASE_PATH%

@set CPACK_PACK_OPTS= 
@set CPACK_PACK_OPTS=%CPACK_PACK_OPTS% --verbose

FOR %%G IN ("%~dp0*.nuspec") DO (
  "%CPACK_EXE%" "%%G" --Version=%VERSION% %CPACK_PACK_OPTS%
  if ERRORLEVEL 1 EXIT /B 1
)

GOTO EOF

:Usage
@ECHO Usage:
@ECHO    CreateOrleansChocolateyPackage ^<Path to Orleans SDK folder^> ^<VersionFile^>

:EOF
