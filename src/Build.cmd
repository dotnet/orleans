@REM NOTE: This script must be run from a Visual Studio command prompt window

@setlocal
@ECHO off

SET CMDHOME=%~dp0.
if "%FrameworkDir%" == "" set FrameworkDir=%WINDIR%\Microsoft.NET\Framework
if "%FrameworkVersion%" == "" set FrameworkVersion=v4.0.30319

SET MSBUILDEXEDIR=%FrameworkDir%\%FrameworkVersion%
SET MSBUILDEXE=%MSBUILDEXEDIR%\MSBuild.exe

set PROJ=%CMDHOME%\Orleans.sln

@echo ===== Building %PROJ% =====

@echo Build Debug ==============================

SET CONFIGURATION=Debug
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

"%MSBUILDEXE%" /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %PROJ%

@echo Build Release ============================

SET CONFIGURATION=Release
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

"%MSBUILDEXE%" /p:Configuration=%CONFIGURATION% "%PROJ%"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for %CONFIGURATION% %PROJ%

@echo Build Release Installers =================

SET CONFIGURATION=Release

set STEP=VSIX
@REM
@REM Install Visual Studio SDK and uncomment the following lines 
@REM to build Visual Studio project templates.
@REM
@REM "%MSBUILDEXE%" /p:Configuration=%CONFIGURATION% "%CMDHOME%\OrleansVSTools\OrleansVSTools.sln"
@REM xcopy /s /y %CMDHOME%\SDK\VSIX %OutDir%\VSIX\
@REM @if ERRORLEVEL 1 GOTO :ErrorStop
@REM @echo BUILD ok for VSIX package for %PROJ%

set STEP=WIX
"%MSBUILDEXE%" /p:Configuration=%CONFIGURATION% /p:OutputPath=. "%CMDHOME%\Build\OrleansSetup.wixproj"
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD ok for WIX package for %PROJ%

@echo ===== Build succeeded for %PROJ% =====
@GOTO :EOF

:ErrorStop
set RC=%ERRORLEVEL%
if "%STEP%" == "" set STEP=%CONFIGURATION%
@echo ===== Build FAILED for %PROJ% -- %STEP% with error %RC% - CANNOT CONTINUE =====
exit /B %RC%
:EOF
