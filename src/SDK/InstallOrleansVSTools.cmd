@setlocal
@prompt $G$S
@set CMDHOME=%~dp0.

@if NOT "%VS150COMNTOOLS%"=="" (
  @set VSTOOLSDIR="%VS150COMNTOOLS%"
  @set VSVER=Visual Studio 2017
) else (
  @if NOT "%VS140COMNTOOLS%"=="" (
    @set VSTOOLSDIR="%VS140COMNTOOLS%"
    @set VSVER=Visual Studio 2015
  ) else (
    @if NOT "%VS120COMNTOOLS%"=="" (
      @set VSTOOLSDIR="%VS120COMNTOOLS%"
      @set VSVER=Visual Studio 2013
    )
  )
)

@set PKG=Orleans Tools for %VSVER%

@echo --- Installing %PKG%
@echo -- VS Tools directory = %VSTOOLSDIR%

@echo - Uninstalling any old copy of Visual Studio extension package %PKG%
%VSTOOLSDIR%\..\IDE\vsixinstaller.exe /q /uninstall:462db41f-31a4-48f0-834c-1bdcc0578511

@echo - Installing Visual Studio extension package %PKG%
%VSTOOLSDIR%\..\IDE\vsixinstaller.exe "%SDKHOME%\VisualStudioTemplates\OrleansVSTools.vsix"

@echo --- Finished installing %PKG%
@pause
