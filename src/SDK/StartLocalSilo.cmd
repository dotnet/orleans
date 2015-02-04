@setlocal
@echo off
@if NOT "%ECHO%"=="" @echo %ECHO%

set CMDDRIVE=%~d0
set CMDHOME=%~dp0.

set LOCAL_SILO_HOME=%CMDHOME%\LocalSilo

@echo == Starting Orleans local silo in %LOCAL_SILO_HOME%

set LOCAL_SILO_EXE=%LOCAL_SILO_HOME%\OrleansHost.exe
set LOCAL_SILO_PARAMS=Primary

cd "%CMDDRIVE%" && cd "%LOCAL_SILO_HOME%" && "%LOCAL_SILO_EXE%" %LOCAL_SILO_PARAMS% %*
