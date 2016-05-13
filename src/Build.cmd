@REM NOTE: This script must be run from a Visual Studio command prompt window

@setlocal
@ECHO off

SET CMDHOME=%~dp0.
if "%VisualStudioVersion%" == "" (
    @REM Try to find VS command prompt init script
    where /Q VsDevCmd.bat
    if ERRORLEVEL 1 (
        if exist "%VS140COMNTOOLS%" (
            call "%VS140COMNTOOLS%VsDevCmd.bat"
        )
    ) else (
        @REM VsDevCmd.bat is in PATH, so just exec it.
        VsDevCmd.bat
    )
)
if "%VisualStudioVersion%" == "" (
    echo Could not determine Visual Studio version in the system. Cannot continue.
    exit /b 1
)
@ECHO VisualStudioVersion = %VisualStudioVersion%

@REM Get path to MSBuild Binaries
if exist "%ProgramFiles%\MSBuild\14.0\bin" SET MSBUILDEXEDIR=%ProgramFiles%\MSBuild\14.0\bin
if exist "%ProgramFiles(x86)%\MSBuild\14.0\bin" SET MSBUILDEXEDIR=%ProgramFiles(x86)%\MSBuild\14.0\bin

@REM Can't multi-block if statement when check condition contains '(' and ')' char, so do as single line checks
if NOT "%MSBUILDEXEDIR%" == "" SET MSBUILDEXE=%MSBUILDEXEDIR%\MSBuild.exe
if NOT "%MSBUILDEXEDIR%" == "" GOTO :MsBuildFound

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

"%MSBUILDEXE%" /nr:False /m /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %PROJ%

@echo Build Release ============================

SET CONFIGURATION=Release
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

"%MSBUILDEXE%" /nr:False /m /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %PROJ%

set STEP=VSIX

if "%VSSDK140Install%" == "" (
    @echo Visual Studio 2015 SDK not installed - Skipping building VSIX
    @GOTO :BuildFinished
)

@echo Build VSIX ============================

set PROJ=%CMDHOME%\OrleansVSTools\OrleansVSTools.sln
SET OutDir=%OutDir%\VSIX
"%MSBUILDEXE%" /nr:False /m /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for VSIX package for %PROJ%

:BuildFinished
@echo ===== Build succeeded for %PROJ% =====
@GOTO :EOF

:ErrorStop
set RC=%ERRORLEVEL%
if "%STEP%" == "" set STEP=%CONFIGURATION%
@echo ===== Build FAILED for %PROJ% -- %STEP% with error %RC% - CANNOT CONTINUE =====
exit /B %RC%
:EOF
