# First build the Orleans vNext nuget packages locally
if((Test-Path "..\..\vNext\Binaries\Debug\") -eq $false) { 
     # this will only work in Windows. 
     # Alternatively build the nuget packages and place them in the <root>/vNext/Binaries/Debug folder 
     # (or make sure there is a package source available with the Orleans 2.0 TP nugets)
    ..\..\Build.cmd netstandard
}

# Uncomment the following to clear the nuget cache if rebuilding the packages doesn't seem to take effect.
#dotnet nuget locals all --clear

dotnet restore
dotnet build

# Run the 2 console apps in different windows

$siloPath = (Get-Item -Path ".\src\SiloHost\" -Verbose).FullName;
$clientPath = (Get-Item -Path ".\src\OrleansClient\" -Verbose).FullName;

Start-Process "dotnet" -ArgumentList "run" -WorkingDirectory $siloPath
Start-Sleep 10
Start-Process "dotnet" -ArgumentList "run" -WorkingDirectory $clientPath
