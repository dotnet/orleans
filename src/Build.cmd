@if not defined _echo @echo off
setlocal

SET CMDHOME=%~dp0.

@REM Locate VS 2017 with the proper method

SET VSWHERE_REMOTE_PATH=https://github.com/Microsoft/vswhere/releases/download/1.0.55/vswhere.exe
SET VSWHERE_LOCAL_PATH=%CMDHOME%\vswhere.exe

if NOT exist "%VSWHERE_LOCAL_PATH%" (
  echo Downloading vswhere.exe from %VSWHERE_REMOTE_PATH%.
  powershell -NoProfile -ExecutionPolicy unrestricted -Command "$retryCount = 0; $success = $false; do { try { (New-Object Net.WebClient).DownloadFile('%VSWHERE_REMOTE_PATH%', '%VSWHERE_LOCAL_PATH%'); $success = $true; } catch { if ($retryCount -ge 6) { throw; } else { $retryCount++; Start-Sleep -Seconds (5 * $retryCount); } } } while ($success -eq $false);"
  if NOT exist "%VSWHERE_LOCAL_PATH%" (
    echo ERROR: Could not install vswhere correctly, falling back to environment variables.
    goto :FallbackEnvVar
  )
)

set pre=Microsoft.VisualStudio.Product.
set ids=%pre%Community %pre%Professional %pre%Enterprise %pre%BuildTools
for /f "usebackq tokens=1* delims=: " %%i in (`%VSWHERE_LOCAL_PATH% -latest -products %ids% -requires Microsoft.Component.MSBuild`) do (
  if /i "%%i"=="installationPath" set VS2017InstallDir=%%j
)

if defined VS2017InstallDir (
  echo Visual Studio 2017 found at "%VS2017InstallDir%".

  if exist "%VS2017InstallDir%\MSBuild\15.0\Bin\MSBuild.exe" (
    set MSBUILDEXEVSIX="%VS2017InstallDir%\MSBuild\15.0\Bin\MSBuild.exe"
  )
)

@REM Old style VS locator

:FallbackEnvVar

if not defined VisualStudioVersion (
    @REM Try to find VS command prompt init script
    where /Q VsDevCmd.bat
        if defined VS140COMNTOOLS (
		call "%VS140COMNTOOLS%\VsDevCmd.bat"
	)
	if not defined VisualStudioVersion (
		echo Could not determine Visual Studio version in the system. Cannot continue.
		exit /b 1
	)
)

@ECHO VisualStudioVersion = %VisualStudioVersion%

@REM Get path to MSBuild Binaries
if exist "%ProgramFiles%\MSBuild\14.0\bin" SET MSBUILDEXEDIR=%ProgramFiles%\MSBuild\14.0\bin
if exist "%ProgramFiles(x86)%\MSBuild\14.0\bin" SET MSBUILDEXEDIR=%ProgramFiles(x86)%\MSBuild\14.0\bin

@REM Can't multi-block if statement when check condition contains '(' and ')' char, so do as single line checks
if defined MSBUILDEXEDIR SET MSBUILDEXE="%MSBUILDEXEDIR%\MSBuild.exe"
if exist "%MSBUILDEXE%" GOTO :MsBuildFound

@REM Try to find VS command prompt init script
where /Q MsBuild.exe
if ERRORLEVEL 1 (
    echo Could not find MSBuild in the system. Cannot continue.
    exit /b 1
) else (
    @REM MsBuild.exe is in PATH, so just use it.
    SET MSBUILDEXE=MSBuild.exe
 )
:MsBuildFound

@ECHO MsBuild Location = %MSBUILDEXE%

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

set PROJ=%CMDHOME%\Orleans.sln

@echo ===== Building %PROJ% =====

@echo Build Debug ==============================

SET CONFIGURATION=Debug
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

%MSBUILDEXE% /nr:False /m /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %PROJ%

@echo Build Release ============================

SET CONFIGURATION=Release
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

%MSBUILDEXE% /nr:False /m /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %PROJ%

REM Build VSIX only if new tooling was found

if "%VS2017InstallDir%" == "" goto :EOF

@echo Build VSIX ============================

set STEP=VSIX
set PROJ=%CMDHOME%\OrleansVSTools\OrleansVSTools.sln
set OutDir=%OutDir%\VSIX

REM Disable CS2008 sine we've no source files in the template projects.

%MSBUILDEXEVSIX% /nr:False /m /p:Configuration=%CONFIGURATION% "%PROJ%" /nowarn:CS2008

@if ERRORLEVEL 1 GOTO :ErrorStop

@echo BUILD ok for VSIX package for %PROJ%
@GOTO :EOF

:ErrorStop
set RC=%ERRORLEVEL%
if "%STEP%" == "" set STEP=%CONFIGURATION%
@echo ===== Build FAILED for %PROJ% -- %STEP% with error %RC% - CANNOT CONTINUE =====
exit /B %RC%

:EOF
