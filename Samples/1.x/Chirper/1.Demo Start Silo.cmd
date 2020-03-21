@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

if exist "%CMDHOME%\Host\bin\Debug\Host.exe" (
cd "%CMDHOME%\Host\bin\Debug\"
Host.exe
) else (
@echo Build Chirper.sln and then run the program
)