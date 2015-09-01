@setlocal
@ECHO off

SET CMDHOME=%~dp0.

if "%FrameworkDir%" == "" set FrameworkDir=%WINDIR%\Microsoft.NET\Framework
if "%FrameworkVersion%" == "" set FrameworkVersion=v4.0.30319

SET MSTEST_EXE_DIR=%VS120COMNTOOLS%..\IDE
SET MSTEST_RUNNER=%MSTEST_EXE_DIR%\MSTest.exe

SET NUNIT_VERSION=2.6.4
SET NUNIT_RUNNER_DIR=%CMDHOME%\packages\NUnit.Runners.%NUNIT_VERSION%\tools
SET NUNIT_RUNNER=%NUNIT_RUNNER_DIR%\nunit-console.exe

SET CONFIGURATION=Release
SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

cd /d "%CMDHOME%"

if .%TEST_CATEGORIES%. == .. set TEST_CATEGORIES=BVT

if exist "%NUNIT_RUNNER%" (
    @echo Using NUnit Runner "%NUGET_RUNNER%" for test categories %TEST_CATEGORIES%

    set TEST_ARGS= /noshadow /framework=4.5 /out=nunit.log
	set TEST_FILES= %OutDir%\Tester.dll %OutDir%\TesterInternal.dll 
    set TEST_GROUPS= /include=%TEST_CATEGORIES%

    set TEST_EXE="%NUNIT_RUNNER%"
) else (
    @echo Using MsTest Runner "%MSTEST_RUNNER%" for test categories %TEST_CATEGORIES%

    set TEST_ARGS= 
	set TEST_FILES= /testcontainer:%OutDir%\Tester.dll /testcontainer:%OutDir%\TesterInternal.dll 
    set TEST_GROUPS= /category:%TEST_CATEGORIES%

    set TEST_EXE="%MSTEST_RUNNER%"
)

@echo Running Test runner command =
@echo %TEST_EXE% %TEST_ARGS% %TEST_GROUPS% %TEST_FILES%

%TEST_EXE% %TEST_ARGS% %TEST_GROUPS% %TEST_FILES%

