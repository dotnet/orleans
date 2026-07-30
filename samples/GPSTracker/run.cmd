@echo off
echo === GPSTracker Sample ===
echo This sample runs a web service and a fake device gateway.
echo Open http://localhost:5001 in your browser to view the map.
echo.
echo Starting service...
start "GPSTracker Service" cmd /k dotnet run --project "%~dp0GPSTracker.Service\GPSTracker.Service.csproj"
echo Waiting for service to start...
timeout /t 5 /nobreak >nul
echo Starting fake device gateway...
dotnet run --project "%~dp0GPSTracker.FakeDeviceGateway\GPSTracker.FakeDeviceGateway.csproj"
