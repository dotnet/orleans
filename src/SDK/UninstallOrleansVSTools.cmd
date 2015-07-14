@setlocal
@prompt $G$S
@set CMDHOME=%~dp0.

@if NOT "%VS120COMNTOOLS%"=="" (
  @set VSTOOLSDIR="%VS120COMNTOOLS%"
  @set VSVER=Visual Studio 2013
) else (
  @set VSTOOLSDIR="%VS110COMNTOOLS%"
  @set VSVER=Visual Studio 2012
)

@set PKG=Orleans Tools for %VSVER%

@echo --- Uninstalling %PKG%
@Echo -- VS Tools directory = %VSTOOLSDIR%

@Echo - Unsetting OrleansSDK environment variable
SetX.exe OrleansSDK ""

@ECHO - Uninstalling Visual Studio extension package %PKG%
%VSTOOLSDIR%\..\IDE\vsixinstaller.exe /uninstall:462db41f-31a4-48f0-834c-1bdcc0578511

@echo --- Finished uninstalling %PKG%
@pause
