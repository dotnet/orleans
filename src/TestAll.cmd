@setlocal
@ECHO on

SET CMDHOME=%~dp0.

@REM Due to more of Windows .cmd script parameter passing quirks, we can't pass this value as cmdline argument, 
@REM  so we need to pass it in through the back door as environment variable, scoped by setlocal
set TEST_CATEGORIES="BVT|Functional"

@REM Note: We transfer _complete_ control to the Test.cmd script here because we don't use call.
"%CMDHOME%\Test.cmd"

@REM Note: Execution will NOT return here, and the exit code returned to the caller will be whatever the other script returned.
