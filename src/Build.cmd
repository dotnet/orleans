@REM NOTE: This script must be run from a Visual Studio command prompt window

@setlocal
@ECHO off

SET CMDHOME=%~dp0.
if "%VisualStudioVersion%" == "" call "%VS140COMNTOOLS%VsDevCmd.bat"
if "%VisualStudioVersion%" == "" (
echo Could not find Visual Studio 14.0 in the system. Cannot continue.
exit /b 1)

rem Get path to MSBuild Binaries
if exist "%ProgramFiles%\MSBuild\14.0\bin" SET MSBUILDEXEDIR=%ProgramFiles%\MSBuild\14.0\bin
if exist "%ProgramFiles(x86)%\MSBuild\14.0\bin" SET MSBUILDEXEDIR=%ProgramFiles(x86)%\MSBuild\14.0\bin
SET MSBUILDEXE=%MSBUILDEXEDIR%\MSBuild.exe

SET VERSION_FILE=%CMDHOME%\Build\Version.txt

if EXIST "%VERSION_FILE%" (
    @Echo Using version number from file %VERSION_FILE%
    FOR /F "usebackq tokens=1,2,3,4 delims=." %%i in (`type "%VERSION_FILE%"`) do set PRODUCT_VERSION=%%i.%%j.%%k
	@Echo PRODUCT_VERSION=%PRODUCT_VERSION%
) else (
    @Echo ERROR: Unable to read version number from file %VERSION_FILE%
    SET PRODUCT_VERSION=1.0
)

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

if "%BuildOrleansNuGet%" == "false" (
    @echo Skipping building VSIX
	@GOTO :EOF
)

set PROJ=%CMDHOME%\OrleansVSTools\OrleansVSTools.sln
SET OutDir=%OutDir%\VSIX
"%MSBUILDEXE%" /nr:False /m /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for VSIX package for %PROJ%

@echo ===== Build succeeded for %PROJ% =====
@GOTO :EOF

:ErrorStop
set RC=%ERRORLEVEL%
if "%STEP%" == "" set STEP=%CONFIGURATION%
@echo ===== Build FAILED for %PROJ% -- %STEP% with error %RC% - CANNOT CONTINUE =====
exit /B %RC%
:EOF
