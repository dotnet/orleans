
REM *********************************************************
REM 
REM     Copyright (c) Microsoft. All rights reserved.
REM     This code is licensed under the Microsoft Public License.
REM     THIS CODE IS PROVIDED *AS IS* WITHOUT WARRANTY OF
REM     ANY KIND, EITHER EXPRESS OR IMPLIED, INCLUDING ANY
REM     IMPLIED WARRANTIES OF FITNESS FOR A PARTICULAR
REM     PURPOSE, MERCHANTABILITY, OR NON-INFRINGEMENT.
REM 
REM *********************************************************

REM Check if the script is running in the Azure emulator and if so do not run
IF "%IsEmulated%"=="true" goto :EOF 

If "%UseServerGC%"=="False" GOTO :ValidateBackground
If "%UseServerGC%"=="0" GOTO :ValidateBackground
SET UseServerGC="True"

:ValidateBackground
If "%UseBackgroundGC%"=="False" GOTO :CommandExecution
If "%UseBackgroundGC%"=="0" GOTO :CommandExecution
SET UseBackgroundGC="True"

:CommandExecution

PowerShell.exe -executionpolicy unrestricted -command ".\GCSettingsManagement.ps1" -serverGC %UseServerGC% -backgroundGC %UseBackgroundGC%

Exit /b