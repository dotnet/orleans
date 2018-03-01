@REM NOTE: This script must be run from a Visual Studio command prompt window

@setlocal
@ECHO off

SET CMDHOME=%~dp0.
if "%FrameworkDir%" == "" set FrameworkDir=%WINDIR%\Microsoft.NET\Framework
if "%FrameworkVersion%" == "" set FrameworkVersion=v4.0.30319

SET MSBUILDEXEDIR=%FrameworkDir%\%FrameworkVersion%
SET MSBUILDEXE=%MSBUILDEXEDIR%\MSBuild.exe

cd %CMDHOME%

FOR /R %%I in (*.sln) do (
  @Echo %%I

  nuget restore %%I

  "%MSBUILDEXE%" /verbosity:quiet %%I
)
