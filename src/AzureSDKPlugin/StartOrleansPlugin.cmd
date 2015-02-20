@setlocal
@echo OFF

REM Copy any configuration files

xcopy ..\..\approot\OrleansConfiguration.xml . /Y
xcopy ..\..\approot\bin\OrleansConfiguration.xml . /Y

REM copy any binaries from the approot folder

xcopy ..\..\approot\*.dll . /Y
xcopy ..\..\approot\bin\*.dll . /Y

REM start the orleans silo

OrleansAzureHost.exe
