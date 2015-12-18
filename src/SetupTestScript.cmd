@REM This script is run automaticaly by the MSBuild framework specified by Local.testsettings
@ECHO on

SET CMDHOME=%~dp0.

if exist "%CMDHOME%\..\..\..\Test" (
  SET DEFAULT_FILE="%CMDHOME%\..\..\..\Test\OrleansTestStorageKey.txt"
) else (
  SET DEFAULT_FILE="%CMDHOME%\..\..\src\Test\OrleansTestStorageKey.txt"
)

if exist "%CMDHOME%\..\..\..\Test" (
  SET DEFAULT_SECRETS_FILE="%CMDHOME%\..\..\..\Test\OrleansTestSecrets.json"
) else (
  SET DEFAULT_SECRETS_FILE="%CMDHOME%\..\..\src\Test\OrleansTestSecrets.json"
)

echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH is %ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH% >> SetupTestScriptOutput.txt 
echo CMDHOME is %CMDHOME% >> SetupTestScriptOutput.txt
echo DEFAULT_FILE is %DEFAULT_FILE% >> SetupTestScriptOutput.txt
echo DEFAULT_SECRETS_FILE is %DEFAULT_SECRETS_FILE% >> SetupTestScriptOutput.txt

if not exist %DEFAULT_FILE% (
echo DEFAULT_FILE %DEFAULT_FILE% does not exist!! >> SetupTestScriptOutput.txt 
) else (
echo DEFAULT_FILE %DEFAULT_FILE% does exist!! >> SetupTestScriptOutput.txt 
)

if not exist %DEFAULT_SECRETS_FILE% (
echo DEFAULT_SECRETS_FILE %DEFAULT_SECRETS_FILE% does not exist!! >> SetupTestScriptOutput.txt 
) else (
echo DEFAULT_SECRETS_FILE %DEFAULT_SECRETS_FILE% does exist!! >> SetupTestScriptOutput.txt 
)

echo "-----------------------------------------------" >> SetupTestScriptOutput.txt 
echo "-----------------------------------------------" >> SetupTestScriptOutput.txt 


if "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%" == "" (

echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH env var is not set. Taking %DEFAULT_FILE%. >> SetupTestScriptOutput.txt
copy /y %DEFAULT_FILE% . >> SetupTestScriptOutput.txt 

) else (

  if not exist "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestStorageKey.txt" (

    echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH env var is set but %ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestStorageKey.txt does not exist. Taking %DEFAULT_FILE%. >> SetupTestScriptOutput.txt
    copy /y %DEFAULT_FILE% . >> SetupTestScriptOutput.txt 

	) else (

	echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH env var is set and found "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestStorageKey.txt". Taking it. >> SetupTestScriptOutput.txt 
	copy /y "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestStorageKey.txt"  . >> SetupTestScriptOutput.txt 

	)

  if not exist "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestSecrets.json" (

    echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH env var is set but %ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestSecrets.json does not exist. Taking %DEFAULT_FILE%. >> SetupTestScriptOutput.txt
    copy /y %DEFAULT_SECRETS_FILE% . >> SetupTestScriptOutput.txt 

	) else (

	echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH env var is set and found "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestSecrets.json". Taking it. >> SetupTestScriptOutput.txt 
	copy /y "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestSecrets.json"  . >> SetupTestScriptOutput.txt 

	)

)

echo "-----------------------------------------------" >> SetupTestScriptOutput.txt 
echo "-----------------------------------------------" >> SetupTestScriptOutput.txt 

set >> SetupTestScriptOutput.txt 