. $PSScriptRoot\common\pipeline-logging-functions.ps1
function Test-FilesUseTelemetryOutput {
    $requireTelemetryExcludeFiles = @(
        "enable-cross-org-publishing.ps1",
        "performance-setup.ps1",
        "retain-build.ps1" )

    $filesMissingTelemetry = Get-ChildItem -File -Recurse -Path "$PSScriptRoot\common" -Include "*.ps1" -Exclude $requireTelemetryExcludeFiles |
        Where-Object { -Not( $_ | Select-String -Pattern "Write-PipelineTelemetryError" )}

    If($filesMissingTelemetry) {
        Write-PipelineTelemetryError -category 'Build' 'One or more eng/common scripts do not use telemetry categorization.'
        Write-Host "See https://github.com/dotnet/arcade/blob/master/Documentation/Projects/DevOps/CI/Telemetry-Guidance.md"
        Write-Host "The following ps1 files do not include telemetry categorization output:"
        ForEach($file In $filesMissingTelemetry) {
            Write-Host $file
        }

        return 1
    }
}

$failOnConfigureToolsetError = $true
$exitCode = Test-FilesUseTelemetryOutput
exit $exitCode