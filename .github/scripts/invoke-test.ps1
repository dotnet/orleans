[CmdletBinding(PositionalBinding = $false)]
param(
    [string] $TestCommand = 'dotnet',

    [string] $ResultDirectory = '.',

    [Parameter(Mandatory)]
    [string] $ResultFilePattern,

    [Parameter(Mandatory)]
    [string[]] $Command
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-TestCommand {
    $messages = [Collections.Generic.List[string]]::new()
    & $TestCommand @Command 2>&1 | ForEach-Object {
        $messages.Add($_.ToString())
        $_ | Out-Host
    }

    return [pscustomobject] @{
        ExitCode = $LASTEXITCODE
        Messages = $messages.ToArray()
    }
}

function Test-IsUninitializedCoordinatorFailure {
    param([string[]] $Messages)

    if (Test-Path -LiteralPath $ResultDirectory) {
        $resultFile = Get-ChildItem -LiteralPath $ResultDirectory -Recurse -File -Filter $ResultFilePattern -ErrorAction SilentlyContinue |
            Select-Object -First 1
        if ($null -ne $resultFile) {
            return $false
        }
    }

    $text = [string]::Join([Environment]::NewLine, $Messages)
    return $text.Contains(
        'Unhandled exception: One or more errors occurred. (Handle is not initialized.)',
        [StringComparison]::Ordinal
    )
}

$result = Invoke-TestCommand
if ($result.ExitCode -ne 0 -and (Test-IsUninitializedCoordinatorFailure $result.Messages)) {
    Write-Warning 'The test coordinator failed with an uninitialized handle before producing results; retrying once.'
    $result = Invoke-TestCommand
}

exit $result.ExitCode
