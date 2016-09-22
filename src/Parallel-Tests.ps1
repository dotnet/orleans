param(
    [string[]] $assemblies,
    [string] $testFilter,
    [string] $outDir)

$xunitRunner = $(Join-Path $PSScriptRoot 'packages\xunit.runner.console.2.1.0\tools\xunit.console.exe ')

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
    $cmdLine = $xunitRunner + $a + ' ' + $testFilter + ' -xml ' + $outXml + ' -parallel none -noshadow -verbose' 
    Write-Host $cmdLine
    Start-Job $ExecuteCmd -ArgumentList $cmdLine -Name $([System.IO.Path]::GetFileNameWithoutExtension($a))
}

Get-Job | Wait-Job | Receive-Job

if (Get-Job -State Failed)
{
    Exit 1
}