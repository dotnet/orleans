@REM NOTE: This script must be run from a Visual Studio command prompt window

@setlocal
@ECHO off

SET CMDHOME=%~dp0.
if "%FrameworkDir%" == "" set FrameworkDir=%WINDIR%\Microsoft.NET\Framework
if "%FrameworkVersion%" == "" set FrameworkVersion=v4.0.30319

SET MSBUILDEXEDIR=%FrameworkDir%\%FrameworkVersion%
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

"%MSBUILDEXE%" /m /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %PROJ%

@echo Build Release ============================

SET CONFIGURATION=Release
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

"%MSBUILDEXE%" /m /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %PROJ%

@echo Build Release Installers =================

SET CONFIGURATION=Release

set STEP=VSIX
@REM
@REM Install Visual Studio SDK and uncomment the following lines 
@REM to build Visual Studio project templates.
@REM
@REM "%MSBUILDEXE%" /m /p:Configuration=%CONFIGURATION% "%CMDHOME%\OrleansVSTools\OrleansVSTools.sln"
@REM xcopy /s /y %CMDHOME%\SDK\VSIX %OutDir%\VSIX\
@REM @if ERRORLEVEL 1 GOTO :ErrorStop
@REM @echo BUILD ok for VSIX package for %PROJ%

@echo ===== Build succeeded for %PROJ% =====
@GOTO :EOF

:ErrorStop
set RC=%ERRORLEVEL%
if "%STEP%" == "" set STEP=%CONFIGURATION%
@echo ===== Build FAILED for %PROJ% -- %STEP% with error %RC% - CANNOT CONTINUE =====
exit /B %RC%
:EOF
