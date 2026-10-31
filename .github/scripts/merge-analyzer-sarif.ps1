[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string] $InputDirectory,

    [Parameter(Mandatory)]
    [string] $OutputFile,

    [Parameter(Mandatory)]
    [string] $AutomationIdPrefix
)

$sarifFiles = @(Get-ChildItem -Path $InputDirectory -Filter '*.sarif' -File | Sort-Object Name)
if ($sarifFiles.Count -eq 0)
{
    throw "No SARIF files were found in '$InputDirectory'."
}

$runs = foreach ($sarifFile in $sarifFiles)
{
    $document = Get-Content -Path $sarifFile.FullName -Raw | ConvertFrom-Json
    foreach ($run in $document.runs)
    {
        $automationDetails = [pscustomobject] @{
            id = "$AutomationIdPrefix/$($sarifFile.BaseName)/"
        }
        $run | Add-Member -MemberType NoteProperty -Name automationDetails -Value $automationDetails -Force
        $run
    }
}

$mergedDocument = [ordered] @{
    version   = '2.1.0'
    '$schema' = 'https://json.schemastore.org/sarif-2.1.0.json'
    runs      = @($runs)
}

$outputDirectory = Split-Path -Path $OutputFile -Parent
New-Item -ItemType Directory -Force $outputDirectory | Out-Null
$mergedDocument | ConvertTo-Json -Depth 100 | Set-Content -Path $OutputFile -Encoding utf8NoBOM
