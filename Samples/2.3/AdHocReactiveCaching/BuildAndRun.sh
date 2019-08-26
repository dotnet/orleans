dotnet restore
dotnet build --no-restore
dotnet run --project ./src/Silo --no-build &
dotnet run --project ./src/Client.A --no-build &
dotnet run --project ./src/Client.B --no-build &
dotnet run --project ./src/Client.C --no-build &