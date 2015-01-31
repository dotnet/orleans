@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

"%CMDHOME%\LoadGenerator\bin\Debug\PresenceLoadGenerator.exe"
