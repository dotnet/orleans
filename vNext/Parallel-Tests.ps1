param(
    [string[]] $assemblies,
    [string] $testFilter,
    [string] $outDir)

# If there is multiple xunit packages installed, take the latest one
$xunitRunner = Get-ChildItem packages -Directory -Filter "xunit.runner.console.*" | 
                ForEach-Object { Get-ChildItem $_.FullName -Recurse -File -Filter "xunit.console.exe" } | 
                Sort-Object -Property VersionInfo | 
                Select-Object -Last 1

$ExecuteCmd =
{
    param($cmd)
    Invoke-Expression $cmd
    if ($LASTEXITCODE -ne 0)
    {
        Throw "Error when running tests"
    }
}

foreach ($a in $assemblies)
{
    $xmlName = 'xUnit-Results-' + [System.IO.Path]::GetFileNameWithoutExtension($a) + '.xml'
    $outXml = $(Join-Path $outDir $xmlName)
    $cmdLine = $xunitRunner.FullName + ' ' + $a + ' ' + $testFilter + ' -xml ' + $outXml + ' -parallel none -noshadow -verbose' 
    Write-Host $cmdLine
    Start-Job $ExecuteCmd -ArgumentList $cmdLine -Name $([System.IO.Path]::GetFileNameWithoutExtension($a))
}

Get-Job | Wait-Job | Receive-Job

if (Get-Job -State Failed)
{
    Exit 1
}