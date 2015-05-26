@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

set USER=44444841

@title ChirperPublisher User=%USER%


if exist "%CMDHOME%\ChirperClient\bin\Debug\Chirper.Client.exe" (
cd "%CMDHOME%\ChirperClient\bin\Debug"
"%CMDHOME%\ChirperClient\bin\Debug\Chirper.Client.exe" /pub %USER%
) else (
@echo Build Chirper.sln and then run the program
pause
)
