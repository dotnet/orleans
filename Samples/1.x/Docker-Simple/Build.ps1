function Test-Command
{
    param
    (
        [Parameter(Mandatory=$True)]
        [string] $command
    )
    if (Get-Command $command -ErrorAction SilentlyContinue)
    {
        Write-Host "- '${command}' command: found"
    }
    else
    {
        Write-Host "- '${command}' command: NOT FOUND"
        exit 1
    }
}

function Test-File-Exists
{
    param
    (
        [Parameter(Mandatory=$True)]
        [string] $file
    )
    if (Test-Path $file)
    {
        Write-Host "- '${file}' file: found"
    }
    else
    {
        Write-Host "- '${file}' file: NOT FOUND"
        exit 1
    }
}

Write-Host -BackgroundColor White -ForegroundColor Black "    Testing prerequisites    "
Test-Command dotnet
Test-Command docker
Test-Command docker-compose
Test-File-Exists connection-string.txt

Write-Host ""
Write-Host -BackgroundColor White -ForegroundColor Black "    Building the solution    "
dotnet publish -c Release -o publish
if (-NOT $LASTEXITCODE -eq 0)
{
    Write-Error "Error during the build"
    exit 1
}

Write-Host ""
Write-Host -BackgroundColor White -ForegroundColor Black "    Building the docker images    "
docker-compose build
if (-NOT $LASTEXITCODE -eq 0)
{
    Write-Error "Error during the build"
    exit 1
}

Write-Host ""
Write-Host -BackgroundColor White -ForegroundColor Black "    Docker images build successful    "