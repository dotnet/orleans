@REM NOTE: This script must be run from a Visual Studio command prompt window

@ECHO off

SET CMDHOME=%~dp0.
if "%FrameworkDir%" == "" set FrameworkDir=%WINDIR%\Microsoft.NET\Framework
if "%FrameworkVersion%" == "" set FrameworkVersion=v4.0.30319

SET MSBUILDEXEDIR=%FrameworkDir%\%FrameworkVersion%
SET MSBUILDEXE=%MSBUILDEXEDIR%\MSBuild.exe

@echo Build Debug ==============================
SET CONFIGURATION=Debug
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

"%MSBUILDEXE%" %CMDHOME%\Orleans.sln
@if ERRORLEVEL 1 GOTO :ErrorStop

@REM Install Visual Studio SDK and uncomment the following lines 
@REM to build Visual Studio project templates.
@REM
@REM %MSBUILDEXE% %CMDHOME%\OrleansVSTools\OrleansVSTools.sln
@REM xcopy /s /y %CMDHOME%\SDK\VSIX %OutDir%\VSIX\
@REM @if ERRORLEVEL 1 GOTO :ErrorStop

"%MSBUILDEXE%" /p:Configuration=%CONFIGURATION% /p:OutputPath=. %CMDHOME%\Build\OrleansSetup.wixproj
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD succeeded for %CONFIGURATION%


@echo Build Release ============================
SET CONFIGURATION=Release
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

"%MSBUILDEXE%" %CMDHOME%\Orleans.sln
@if ERRORLEVEL 1 GOTO :ErrorStop

@REM Install Visual Studio SDK and uncomment the following lines 
@REM to build Visual Studio project templates.
@REM
@REM %MSBUILDEXE% %CMDHOME%\OrleansVSTools\OrleansVSTools.sln
@REM xcopy /s /y %CMDHOME%\SDK\VSIX %OutDir%\VSIX\
@REM @if ERRORLEVEL 1 GOTO :ErrorStop

"%MSBUILDEXE%" /p:Configuration=%CONFIGURATION% /p:OutputPath=. %CMDHOME%\Build\OrleansSetup.wixproj
@if ERRORLEVEL 1 GOTO :ErrorStop
@echo BUILD succeeded for %CONFIGURATION%
@GOTO :EOF

:ErrorStop
@echo BUILD FAILED for %CONFIGURATION% with error %ERRORLEVEL% - CANNOT CONTINUE

:EOF
