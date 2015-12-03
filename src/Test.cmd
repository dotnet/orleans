@setlocal
@ECHO off

if .%TEST_CATEGORIES%. == .. set TEST_CATEGORIES="TestCategory=BVT"

SET CONFIGURATION=Release

SET CMDHOME=%~dp0
@REM Remove trailing backslash \
set CMDHOME=%CMDHOME:~0,-1%

if "%FrameworkDir%" == "" set FrameworkDir=%WINDIR%\Microsoft.NET\Framework
if "%FrameworkVersion%" == "" set FrameworkVersion=v4.0.30319

if NOT "%VS120COMNTOOLS%" == "" set VSIDEDIR=%VS120COMNTOOLS%..\IDE
if NOT "%VS140COMNTOOLS%" == "" set VSIDEDIR=%VS140COMNTOOLS%..\IDE
SET VSTESTEXEDIR=%VSIDEDIR%\CommonExtensions\Microsoft\TestWindow
SET VSTESTEXE=%VSTESTEXEDIR%\VSTest.console.exe

cd "%CMDHOME%"
@cd

SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

set TESTS=%OutDir%\Tester.dll %OutDir%\TesterInternal.dll 
@Echo Test assemblies = %TESTS%

set TEST_ARGS= /Settings:%CMDHOME%\Local.testsettings
set TEST_ARGS= %TEST_ARGS% /TestCaseFilter:%TEST_CATEGORIES%

@echo on

"%VSTESTEXE%" %TEST_ARGS% %TESTS%
