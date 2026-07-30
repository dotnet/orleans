#!/bin/bash
SCRIPT_DIR="$(dirname "$0")"
echo "=== CustomDataAdapter Streaming Sample ==="
echo "This sample demonstrates custom data adapters for non-Orleans clients."
echo ""
echo "Starting Silo in background..."
dotnet run --project "$SCRIPT_DIR/Silo/Silo.csproj" &
SERVER_PID=$!
echo "Waiting for silo to start..."
sleep 5
echo "Starting NonOrleansClient..."
dotnet run --project "$SCRIPT_DIR/NonOrleansClient/NonOrleansClient.csproj"
echo "Stopping silo..."
kill $SERVER_PID 2>/dev/null
