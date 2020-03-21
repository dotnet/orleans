@if not defined _echo @echo off
setlocal enabledelayedexpansion

:: Locate dotnet.exe, we're processing multi-line output of where.exe and only matching the first tag of the 
:: found version number

set /p REQUIRED_DOTNET_VERSION=< "%~dp0DotnetCLIVersion.txt"

echo .Net Core version required: %REQUIRED_DOTNET_VERSION%

for /f "tokens=*" %%i in ('where dotnet.exe') do (
  set INSTALLED_DOTNET_EXE=%%i

  echo Found dotnet.exe at: "!INSTALLED_DOTNET_EXE!"

  for /f "tokens=*" %%j in ('"!INSTALLED_DOTNET_EXE!" --version') do set INSTALLED_DOTNET_VERSION=%%j

  if [!REQUIRED_DOTNET_VERSION!]==[!INSTALLED_DOTNET_VERSION!] (

    echo .Net Core major version is matching !INSTALLED_DOTNET_VERSION!, using the installed version.

    set local_dotnet="!INSTALLED_DOTNET_EXE!"

    goto :dotnet-installed
  )
)

:install-dotnet

:: Restore the Tools directory
call %~dp0init-tools.cmd
if NOT [%ERRORLEVEL%]==[0] exit /b 1

endlocal && set _dotnet=%~dp0Tools\dotnetcli\dotnet.exe

goto :eof

:dotnet-installed

endlocal && set _dotnet=%local_dotnet%

:eof
