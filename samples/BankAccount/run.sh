#!/bin/bash
SCRIPT_DIR="$(dirname "$0")"
echo "=== BankAccount Sample ==="
echo "This sample requires running both server and client."
echo ""
echo "Starting server in background..."
dotnet run --project "$SCRIPT_DIR/BankServer/BankServer.csproj" &
SERVER_PID=$!
echo "Waiting for server to start..."
sleep 5
echo "Starting client..."
dotnet run --project "$SCRIPT_DIR/BankClient/BankClient.csproj"
echo "Stopping server..."
kill $SERVER_PID 2>/dev/null
