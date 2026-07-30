#!/bin/bash
SCRIPT_DIR="$(dirname "$0")"
echo "=== Presence Sample ==="
echo "This sample has 3 components:"
echo "  - PresenceService: The Orleans silo"
echo "  - LoadGenerator: Generates simulated player activity"
echo "  - PlayerWatcher: Watches player presence changes"
echo ""
echo "Starting PresenceService in background..."
dotnet run --project "$SCRIPT_DIR/src/PresenceService/PresenceService.csproj" &
SERVICE_PID=$!
echo "Waiting for service to start..."
sleep 5
echo "Starting LoadGenerator in background..."
dotnet run --project "$SCRIPT_DIR/src/LoadGenerator/LoadGenerator.csproj" &
LOAD_PID=$!
echo "Starting PlayerWatcher..."
dotnet run --project "$SCRIPT_DIR/src/PlayerWatcher/PlayerWatcher.csproj"
echo "Stopping background processes..."
kill $LOAD_PID $SERVICE_PID 2>/dev/null
