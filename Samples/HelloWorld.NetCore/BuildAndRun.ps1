# First build the Orleans vNext nuget packages locally
if((Test-Path "..\..\vNext\Binaries\Debug\") -eq $false) { 
     # this will only work in Windows. 
     # Alternatively build the nuget packages and place them in the <root>/vNext/Binaries/Debug folder 
     # (or make sure there is a package source available with the Orleans 2.0 TP nugets)
    #..\..\Build.cmd netstandard
}

# Uncomment the following to clear the nuget cache if rebuilding the packages doesn't seem to take effect.
#dotnet nuget locals all --clear

dotnet restore
if ($LastExitCode -ne 0) { return; }

dotnet publish --no-restore
if ($LastExitCode -ne 0) { return; }

# Run the 2 console apps in different windows

$siloPath = (Get-Item -Path ".\src\SiloHost\bin\Debug\netcoreapp2.0\publish\" -Verbose).FullName;
$clientPath = (Get-Item -Path ".\src\OrleansClient\bin\Debug\netcoreapp2.0\publish\" -Verbose).FullName;

Start-Process "dotnet" -ArgumentList "SiloHost.dll" -WorkingDirectory $siloPath
Start-Sleep 10
Start-Process "dotnet" -ArgumentList "OrleansClient.dll" -WorkingDirectory $clientPath
