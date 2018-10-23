dotnet restore
if ($LastExitCode -ne 0) { return; }

dotnet build --no-restore
if ($LastExitCode -ne 0) { return; }

# Run the 2 console apps in different windows

Start-Process "dotnet" -ArgumentList "run --project src/SiloHost --no-build"
