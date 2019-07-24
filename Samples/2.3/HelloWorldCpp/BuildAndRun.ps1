# First build the Orleans vNext nuget packages locally
if((Test-Path "..\..\vNext\Binaries\Debug\") -eq $false) {
     # this will only work in Windows.
     # Alternatively build the nuget packages and place them in the <root>\vNext\Binaries\Debug folder
     # (or make sure there is a package source available with the Orleans 2.0 TP nugets)
    #..\..\Build.cmd netstandard
}

# Uncomment the following to clear the nuget cache if rebuilding the packages doesn't seem to take effect.
#dotnet nuget locals all --clear

Write-Host "Restoring packages..."
dotnet restore HelloWorld.Build.sln
if ($LastExitCode -ne 0) { return; }

Write-Host "Building solution..."
dotnet build HelloWorld.Build.sln --no-restore
if ($LastExitCode -ne 0) { return; }

Write-Host "Building C++ Client..."
Start-Process -FilePath ".\src\OrleansClientCpp\build.bat" -WorkingDirectory ".\src\OrleansClientCpp\" -Wait
if ($LastExitCode -ne 0) { return; }

# Run the 2 console apps in different windows

Write-Host "Starting Silo"
Start-Process "dotnet" -ArgumentList ".\src\SiloHost\bin\Debug\netcoreapp2.1\SiloHost.dll" 
Start-Sleep 10
Write-Host "Starting C++ Client"
Start-Process ".\src\OrleansClientCpp\bin\windows\ClientCpp.exe" -WorkingDirectory ".\src\OrleansClientCpp\bin\windows"