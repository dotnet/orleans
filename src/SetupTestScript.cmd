@REM This script is run automaticaly by the MSBuild framework specified by Local.testsettings
@ECHO off
setlocal EnableDelayedExpansion
SET CMDHOME=%~dp0
if not [%1]==[] (SET TargetDir=%1%) else (SET TargetDir=.)
if not [%2]==[] pushd %2

echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH is %ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH% >> SetupTestScriptOutput.txt 
echo CMDHOME is %CMDHOME% >> SetupTestScriptOutput.txt
echo Current directory is "%CD%" (search will be started from here) >> SetupTestScriptOutput.txt
echo Target directory is "%TargetDir%" (file will be copied here) >> SetupTestScriptOutput.txt

if NOT "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%" == "" (
  if exist "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestSecrets.json" (
	SET SECRETS_FILE=%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestSecrets.json
	echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH env var is set and found "!SECRETS_FILE!". Taking it. >> SetupTestScriptOutput.txt
	goto Copy
  ) else (
	echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH env var is set but not secret files where found in them. >> SetupTestScriptOutput.txt
  )
) else (
  echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH env var is not set. >> SetupTestScriptOutput.txt
)

call:GetDirectoryNameOfFileAbove OrleansTestSecrets.json
IF NOT "!result!"=="" (
   SET SECRETS_FILE=!result!OrleansTestSecrets.json
)

:Copy
echo "-----------------------------------------------" >> SetupTestScriptOutput.txt 
if not exist "!SECRETS_FILE!" (
	echo SECRETS_FILE !SECRETS_FILE! does not exist. No secrets will be copied. >> SetupTestScriptOutput.txt 
) else (
	echo SECRETS_FILE !SECRETS_FILE! exists and will be copied. >> SetupTestScriptOutput.txt 
	copy /y "!SECRETS_FILE!" %TargetDir% >> SetupTestScriptOutput.txt 
)

echo "-----------------------------------------------" >> SetupTestScriptOutput.txt 
set >> SetupTestScriptOutput.txt

if not [%2]==[] popd
endlocal
goto:eof

:GetDirectoryNameOfFileAbove
@echo off
REM This script emulates the behavior of the GetDirectoryNameOfFileAbove MSBuild property function that finds 
REM the first ancestor directory that includes certain file, and returns the full path to that directory.
REM the resulting path is set in the %result% variable
setlocal EnableDelayedExpansion
set "theFile=%~1"
rem Get the path of this Batch file, i.e. "c:\projects\everything\foo\bar\"
set "myPath=%~DP0"
rem Process it as a series of ancestor directories
rem i.e. "c:\" "c:\projects\" "c:\projects\everything\" etc...
rem and search the file in each ancestor, taking as result the last one

set "result="
set "thisParent="
set "myPath=%myPath:~0,-1%"
for %%a in ("%myPath:\=" "%") do (
   set "thisParent=!thisParent!%%~a\"
   if exist "!thisParent!%theFile%" set "result=!thisParent!"
)
endlocal&set result=%result%
goto:eof



