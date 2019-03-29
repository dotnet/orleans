# restore package
dotnet restore
if ($LastExitCode -ne 0) { return; }

# build the solution
dotnet build --no-restore
if ($LastExitCode -ne 0) { return; }

# start the silo on its own process
Start-Process "dotnet" -ArgumentList "run --project src/Silo --no-build"

# start the first client on its own process
Start-Process "dotnet" -ArgumentList "run --project src/Client.A --no-build"

# start the second client on its own process
Start-Process "dotnet" -ArgumentList "run --project src/Client.B --no-build"

# start the third client on its own process
Start-Process "dotnet" -ArgumentList "run --project src/Client.C --no-build"
