@REM This script is run automaticaly by the MSBuild framework specified by Local.testsettings
@ECHO on

SET CMDHOME=%~dp0.
SET DEFAULT_FILE="%CMDHOME%\..\..\..\Test\OrleansTestStorageKey.txt"


@echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH is %ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH% >> SetupTestScriptOutput.txt 
@echo CMDHOME is %CMDHOME% >> SetupTestScriptOutput.txt
@echo DEFAULT_FILE is %DEFAULT_FILE% >> SetupTestScriptOutput.txt

if not exist %DEFAULT_FILE% (
@echo DEFAULT_FILE %DEFAULT_FILE% does not exist!!
) else (
@echo DEFAULT_FILE %DEFAULT_FILE% does exist!!
)

echo "-----------------------------------------------" >> SetupTestScriptOutput.txt 
echo "-----------------------------------------------" >> SetupTestScriptOutput.txt 


if "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%" == "" (

@echo ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH env var is not set. Taking %DEFAULT_FILE%. >> SetupTestScriptOutput.txt
copy /y %DEFAULT_FILE% .

) else if not exist "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestStorageKey.txt" (

@echo %ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestStorageKey.txt does not exist. Taking %DEFAULT_FILE%. >> SetupTestScriptOutput.txt
copy /y %DEFAULT_FILE% .

) else (

@echo Found "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestStorageKey.txt" and taking it.
copy /y "%ORLEANS_TEST_STORAGE_KEY_FOLDER_PATH%\OrleansTestStorageKey.txt"  . >> SetupTestScriptOutput.txt 

)


echo "-----------------------------------------------" >> SetupTestScriptOutput.txt 
echo "-----------------------------------------------" >> SetupTestScriptOutput.txt 

set >> SetupTestScriptOutput.txt 