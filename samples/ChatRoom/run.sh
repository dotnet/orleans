#!/bin/bash
SCRIPT_DIR="$(dirname "$0")"
echo "=== ChatRoom Sample ==="
echo "This sample requires running both service and client."
echo ""
echo "Starting service in background..."
dotnet run --project "$SCRIPT_DIR/ChatRoom.Service/ChatRoom.Service.csproj" &
SERVER_PID=$!
echo "Waiting for service to start..."
sleep 5
echo "Starting client..."
dotnet run --project "$SCRIPT_DIR/ChatRoom.Client/ChatRoom.Client.csproj"
echo "Stopping service..."
kill $SERVER_PID 2>/dev/null
