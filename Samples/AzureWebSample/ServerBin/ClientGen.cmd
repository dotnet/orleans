@setlocal
@REM @echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

"%CMDHOME%\ClientGenerator.exe" %*
