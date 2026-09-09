[CmdletBinding()]
param(
    [string] $RestoreCommand = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-SolutionRestore {
    $messages = [Collections.Generic.List[string]]::new()
    & $RestoreCommand restore Orleans.slnx 2>&1 | ForEach-Object {
        $messages.Add($_.ToString())
        $_ | Out-Host
    }

    return [pscustomobject] @{
        ExitCode = $LASTEXITCODE
        Messages = $messages.ToArray()
    }
}

function Test-IsRemoteMetadataFailure {
    param([string[]] $Messages)

    $text = [string]::Join([Environment]::NewLine, $Messages)
    return $text.Contains(
        'Failed to retrieve information about ',
        [StringComparison]::Ordinal
    ) -and $text.Contains(
        ' from remote source ',
        [StringComparison]::Ordinal
    )
}

$result = Invoke-SolutionRestore
if ($result.ExitCode -eq 0 -or -not (Test-IsRemoteMetadataFailure $result.Messages)) {
    exit $result.ExitCode
}

Write-Warning 'Retrying restore after a remote package metadata retrieval failure.'
$result = Invoke-SolutionRestore
exit $result.ExitCode
