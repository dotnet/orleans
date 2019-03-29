dotnet restore
dotnet build --no-restore
dotnet run --project ./src/Silo --no-build &
dotnet run --project ./src/PlayerWatcher --no-build &
dotnet run --project ./src/LoadGenerator --no-build &