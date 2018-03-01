@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDHOME=%~dp0.

cd "%CMDHOME%\Host\bin\Debug"

"%CMDHOME%\Host\bin\Debug\Host.exe"
