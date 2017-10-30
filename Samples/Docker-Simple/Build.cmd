@echo off

echo [7mTesting prerequisites[0m
:: Check if dotnet.exe is installed
for /f "tokens=*" %%i in ('where dotnet.exe') do (
  echo - 'dotnet.exe' command: found
  goto :dotnet-found
)
echo - 'dotnet.exe' command: [101;93mNOT FOUND[0m
goto :ErrorStop
:dotnet-found
:: Check if dotnet-compose is installed
for /f "tokens=*" %%i in ('where docker-compose') do (
  echo - 'docker-compose' command: found
  goto :docker-compose-found
 )
echo - 'docker-compose' command: [101;93mNOT FOUND[0m
goto :ErrorStop
:docker-compose-found
:: Check if the file with the connection string exists
if exist connection-string.txt (
  echo - 'connection-string.txt' file: found
) else (
  echo - 'connection-string.txt' file: [101;93mNOT FOUND[0m
  goto :ErrorStop
)

:: Publishing the solution with dotnet
echo. 
echo [7mBuilding the solution[0m
call dotnet publish -c Release -o publish
@if ERRORLEVEL 1 GOTO :ErrorStop

:: Building the docker images with docker-compose
echo.
echo [7mBuilding the docker images[0m
call docker-compose build
@if ERRORLEVEL 1 GOTO :ErrorStop

:: Everything is fine!
echo.
echo [7mDocker images build successful[0m
goto :EOF

:: Something bad happened
:ErrorStop
echo.
echo [101;93mError during the build[0m
exit /B %RC%

:EOF