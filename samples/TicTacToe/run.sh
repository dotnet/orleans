#!/bin/bash
echo "Building and running TicTacToe sample..."
echo "Open https://localhost:5000 in your browser"
dotnet run --project "$(dirname "$0")/TicTacToe.csproj"
