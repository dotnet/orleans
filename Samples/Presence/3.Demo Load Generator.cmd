@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

cd "%CMDHOME%\LoadGenerator\bin\Debug"

"%CMDHOME%\LoadGenerator\bin\Debug\PresenceLoadGenerator.exe"
