@setlocal
@ECHO off

SET CONFIGURATION=Release

SET CMDHOME=%~dp0
@REM Remove trailing backslash \
set CMDHOME=%CMDHOME:~0,-1%

pushd "%CMDHOME%"
@cd

SET OutDir=%CMDHOME%\..\Binaries\%CONFIGURATION%

set TESTS=%OutDir%\Tester.dll %OutDir%\TesterInternal.dll
if []==[%TEST_FILTERS%] set TEST_FILTERS=-trait "Category=BVT"

@Echo Test assemblies = %TESTS%
@Echo Test filters = %TEST_FILTERS%
@echo on
call "%CMDHOME%\SetupTestScript.cmd" "%OutDir%"

packages\xunit.runner.console.2.1.0\tools\xunit.console %TESTS% %TEST_FILTERS% -xml "%OutDir%/xUnit-Results.xml" -parallel none -noshadow
set testresult=%errorlevel%
popd
endlocal&set testresult=%testresult%
exit /B %testresult%