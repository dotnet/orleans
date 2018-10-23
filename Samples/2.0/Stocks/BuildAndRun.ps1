dotnet restore
if ($LastExitCode -ne 0) { return; }

dotnet build --no-restore
if ($LastExitCode -ne 0) { return; }

Start-Process "dotnet" -ArgumentList "run --project src/SiloHost --no-build"
