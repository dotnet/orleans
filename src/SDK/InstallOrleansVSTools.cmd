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

@echo --- Installing %PKG%
@Echo -- VS Tools directory = %VSTOOLSDIR%

@set SDKHOME=%CMDHOME%
@Echo - Setting OrleansSDK environment variable to %SDKHOME%
SetX.exe OrleansSDK "%SDKHOME%"

@Echo - Removing any old copy of Visual Studio extension package %PKG%
%VSTOOLSDIR%\..\IDE\vsixinstaller.exe /q /uninstall:462db41f-31a4-48f0-834c-1bdcc0578511

@Echo - Installing Visual Studio extension package %PKG%
%VSTOOLSDIR%\..\IDE\vsixinstaller.exe "%SDKHOME%\VisualStudioTemplates\OrleansVSTools.vsix"

@echo --- Finished installing %PKG%
@pause
