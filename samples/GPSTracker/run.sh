#!/bin/bash
SCRIPT_DIR="$(dirname "$0")"
echo "=== GPSTracker Sample ==="
echo "This sample runs a web service and a fake device gateway."
echo "Open http://localhost:5001 in your browser to view the map."
echo ""
echo "Starting service in background..."
dotnet run --project "$SCRIPT_DIR/GPSTracker.Service/GPSTracker.Service.csproj" &
SERVER_PID=$!
echo "Waiting for service to start..."
sleep 5
echo "Starting fake device gateway..."
dotnet run --project "$SCRIPT_DIR/GPSTracker.FakeDeviceGateway/GPSTracker.FakeDeviceGateway.csproj"
echo "Stopping service..."
kill $SERVER_PID 2>/dev/null
