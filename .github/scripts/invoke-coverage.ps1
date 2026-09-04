[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory)]
    [string] $Settings,

    [Parameter(Mandatory)]
    [string] $Output,

    [string] $RetryLogFile,

    [string] $CoverageCommand = 'dotnet-coverage',

    [Parameter(Mandatory)]
    [string[]] $Command
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Assert-NotReparsePoint {
    param([string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        return
    }

    $item = Get-Item -LiteralPath $Path -Force
    $linkType = $item.PSObject.Properties['LinkType']
    if (($linkType -and $linkType.Value) -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw "$Path must not be a symbolic link"
    }
}

$outputDirectory = Split-Path -Parent $Output

function Assert-CoverageOutputPath {
    if (-not [string]::IsNullOrWhiteSpace($outputDirectory)) {
        Assert-NotReparsePoint $outputDirectory
    }

    Assert-NotReparsePoint $Output
}

function Invoke-Collector {
    param([switch] $EnableVerboseLog)

    Assert-CoverageOutputPath
    $arguments = [Collections.Generic.List[string]]::new()
    $arguments.Add('collect')
    $arguments.Add('--settings')
    $arguments.Add($Settings)
    $arguments.Add('--output')
    $arguments.Add($Output)
    $arguments.Add('--output-format')
    $arguments.Add('cobertura')
    $arguments.Add('--nologo')

    if ($EnableVerboseLog -and -not [string]::IsNullOrWhiteSpace($RetryLogFile)) {
        $logDirectory = Split-Path -Parent $RetryLogFile
        if (-not [string]::IsNullOrWhiteSpace($logDirectory)) {
            Assert-NotReparsePoint $logDirectory
            [void] (New-Item -ItemType Directory -Force -Path $logDirectory)
            Assert-NotReparsePoint $logDirectory
        }

        Assert-NotReparsePoint $RetryLogFile
        Remove-Item -LiteralPath $RetryLogFile -Force -ErrorAction SilentlyContinue
        $arguments.Add('--log-file')
        $arguments.Add($RetryLogFile)
        $arguments.Add('--log-level')
        $arguments.Add('Verbose')
    }

    $arguments.AddRange($Command)
    $messages = [Collections.Generic.List[string]]::new()
    & $CoverageCommand @arguments 2>&1 | ForEach-Object {
        $messages.Add($_.ToString())
        $_ | Out-Host
    }

    return [pscustomobject] @{
        ExitCode = $LASTEXITCODE
        Messages = $messages.ToArray()
    }
}

function Test-IsUninitializedHandleFailure {
    param([string[]] $Messages)

    $text = [string]::Join([Environment]::NewLine, $Messages)
    return $text.Contains(
        'One or more errors occurred. (Handle is not initialized.)',
        [StringComparison]::Ordinal
    ) -and $text.Contains(
        'No code coverage data available. Profiler was not initialized.',
        [StringComparison]::Ordinal
    )
}

$result = Invoke-Collector
if ($result.ExitCode -ne 0 -and (Test-IsUninitializedHandleFailure $result.Messages)) {
    Write-Warning 'The coverage profiler failed with an uninitialized handle; retrying collection once.'
    Assert-CoverageOutputPath
    Remove-Item -LiteralPath $Output -Force -ErrorAction SilentlyContinue
    $result = Invoke-Collector -EnableVerboseLog
}

exit $result.ExitCode
