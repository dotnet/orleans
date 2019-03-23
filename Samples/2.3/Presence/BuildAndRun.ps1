# restore package
dotnet restore
if ($LastExitCode -ne 0) { return; }

# build the solution
dotnet build --no-restore
if ($LastExitCode -ne 0) { return; }

# start the silo on its own process
Start-Process "dotnet" -ArgumentList "run --project src/Silo --no-build"

# start the player watcher on its own process
Start-Process "dotnet" -ArgumentList "run --project src/PlayerWatcher --no-build"

# start the load generator on its own process
Start-Process "dotnet" -ArgumentList "run --project src/LoadGenerator --no-build"
