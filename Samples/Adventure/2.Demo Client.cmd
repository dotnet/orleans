@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

if exist "%CMDHOME%\AdventureClient\bin\Debug\AdventureClient.exe" (
cd /d "%CMDHOME%\AdventureClient\bin\Debug"
AdventureClient.exe
cd "%CMDHOME%"
) else (
@echo Build Adventure.sln and then run the program
)