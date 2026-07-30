#!/bin/bash
SCRIPT_DIR="$(dirname "$0")"
echo "=== Simple Streaming Sample ==="
echo "This sample demonstrates basic Orleans streaming."
echo ""
echo "Starting SiloHost in background..."
dotnet run --project "$SCRIPT_DIR/SiloHost/SiloHost.csproj" &
SERVER_PID=$!
echo "Waiting for silo to start..."
sleep 5
echo "Starting Client..."
dotnet run --project "$SCRIPT_DIR/Client/Client.csproj"
echo "Stopping silo..."
kill $SERVER_PID 2>/dev/null
