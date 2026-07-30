@echo off
echo === Streaming Samples ===
echo Choose which streaming sample to run:
echo   1. Simple - Basic streaming with Orleans streams
echo   2. CustomDataAdapter - Custom data adapter for non-Orleans clients
echo.
set /p choice="Enter choice (1 or 2): "
if "%choice%"=="1" (
    call "%~dp0Simple\run.cmd"
) else if "%choice%"=="2" (
    call "%~dp0CustomDataAdapter\run.cmd"
) else (
    echo Invalid choice. Running Simple sample by default...
    call "%~dp0Simple\run.cmd"
)
