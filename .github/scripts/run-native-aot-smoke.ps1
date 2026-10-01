[CmdletBinding(DefaultParameterSetName = 'Run')]
param(
    [Parameter(Mandatory, ParameterSetName = 'List')]
    [switch] $List,

    [Parameter(Mandatory, ParameterSetName = 'Run')]
    [string] $Scenario,

    [Parameter(Mandatory, ParameterSetName = 'Run')]
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $RuntimeIdentifier,

    [Parameter(ParameterSetName = 'Run')]
    [string] $ResultsDirectory,

    [Parameter(ParameterSetName = 'Run')]
    [switch] $UseEnvironmentalTools
)

$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$projectDirectory = Join-Path (Join-Path $repositoryRoot 'test') 'Orleans.NativeAotSmoke'
$manifests = @(Get-ChildItem -LiteralPath $projectDirectory -Filter '*.smoke.json' -File | Sort-Object Name)
if ($manifests.Count -eq 0)
{
    throw "No native smoke scenario manifests were found in '$projectDirectory'."
}

$names = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$scenarios = foreach ($file in $manifests)
{
    $manifest = Get-Content -LiteralPath $file.FullName -Raw | ConvertFrom-Json -AsHashtable
    foreach ($property in @('scenario', 'diagnostics', 'rejectDiagnostics'))
    {
        if (-not $manifest.ContainsKey($property))
        {
            throw "Manifest '$($file.Name)' is missing '$property'."
        }
    }

    foreach ($property in $manifest.Keys)
    {
        if ($property -notin @('scenario', 'diagnostics', 'rejectDiagnostics'))
        {
            throw "Manifest '$($file.Name)' has unknown property '$property'."
        }
    }

    if ($manifest.scenario -isnot [string] -or
        $manifest.scenario -cnotmatch '^[A-Za-z][A-Za-z0-9]*$' -or
        $file.Name -cne "$($manifest.scenario).smoke.json")
    {
        throw "Manifest '$($file.Name)' must name its alphanumeric scenario exactly."
    }

    if (-not $names.Add($manifest.scenario))
    {
        throw "Native smoke scenario '$($manifest.scenario)' is registered more than once."
    }

    if ($manifest.diagnostics -notin @('strict', 'legacy-visible') -or
        $manifest.rejectDiagnostics -isnot [array])
    {
        throw "Manifest '$($file.Name)' must specify a diagnostic policy and an array of rejection patterns."
    }

    if ($manifest.diagnostics -eq 'legacy-visible' -and $manifest.rejectDiagnostics.Count -eq 0)
    {
        throw "Manifest '$($file.Name)' must gate its supported path while retaining legacy diagnostics."
    }

    foreach ($pattern in $manifest.rejectDiagnostics)
    {
        if ($pattern -isnot [string] -or [string]::IsNullOrWhiteSpace($pattern))
        {
            throw "Manifest '$($file.Name)' contains an empty diagnostic rejection pattern."
        }

        [void] [regex]::new($pattern)
    }

    if ($manifest.scenario -ne 'DependencyInjection' -and
        @(Get-ChildItem -LiteralPath $projectDirectory -Filter "$($manifest.scenario)*.cs" -File).Count -eq 0)
    {
        throw "Manifest '$($file.Name)' has no matching scenario source files."
    }

    $manifest
}

if ($List)
{
    $include = foreach ($manifest in $scenarios)
    {
        [ordered] @{ scenario = $manifest.scenario; runner = 'ubuntu-latest'; runtime = 'linux-x64' }
        [ordered] @{ scenario = $manifest.scenario; runner = 'windows-latest'; runtime = 'win-x64' }
    }

    [ordered] @{ include = @($include) } | ConvertTo-Json -Depth 4 -Compress
    return
}

$selected = @($scenarios | Where-Object { $_.scenario -ceq $Scenario })
if ($selected.Count -ne 1)
{
    throw "Native smoke scenario '$Scenario' is not registered."
}

$selected = $selected[0]
if ([string]::IsNullOrWhiteSpace($ResultsDirectory))
{
    $ResultsDirectory = Join-Path (Join-Path (Join-Path $repositoryRoot 'Artifacts') 'NativeAotSmoke') "$Scenario-$RuntimeIdentifier"
}

$ResultsDirectory = [System.IO.Path]::GetFullPath($ResultsDirectory)
New-Item -ItemType Directory -Force -Path $ResultsDirectory | Out-Null
$output = Join-Path $ResultsDirectory 'native'
$publishLog = Join-Path $ResultsDirectory 'publish.log'
$runtimeLog = Join-Path $ResultsDirectory 'runtime.log'
$publishArguments = @(
    'publish', (Join-Path $projectDirectory 'Orleans.NativeAotSmoke.csproj'),
    '--configuration', 'Release',
    '--framework', 'net10.0',
    '--runtime', $RuntimeIdentifier,
    '--self-contained', 'true',
    '--output', $output,
    "-p:NativeAotSmokeScenario=$Scenario",
    '-p:TrimmerSingleWarn=false',
    '-m:1',
    "-bl:$(Join-Path $ResultsDirectory 'publish.binlog')"
)

if ($UseEnvironmentalTools)
{
    $publishArguments += '-p:IlcUseEnvironmentalTools=true'
}

if ($selected.diagnostics -eq 'legacy-visible')
{
    $publishArguments += @(
        '-p:ILLinkTreatWarningsAsErrors=false',
        '-p:IlcTreatWarningsAsErrors=false',
        '-p:WarningsNotAsErrors=IL2026%3BIL2055%3BIL2057%3BIL2060%3BIL2067%3BIL2070%3BIL2071%3BIL2077%3BIL2090%3BIL2091%3BIL2096%3BIL3050%3BIL4000'
    )
}
else
{
    $publishArguments += @(
        '-p:TreatWarningsAsErrors=true',
        '-p:ILLinkTreatWarningsAsErrors=true',
        '-p:IlcTreatWarningsAsErrors=true'
    )
}

& dotnet @publishArguments *>&1 | Tee-Object -FilePath $publishLog
$publishExitCode = $LASTEXITCODE
if ($publishExitCode -ne 0)
{
    throw "Native smoke '$Scenario' publish failed with exit code $publishExitCode. See '$publishLog'."
}

$diagnostics = @(Select-String -LiteralPath $publishLog -Pattern '(warning|error)\s+IL[0-9]+')
foreach ($pattern in $selected.rejectDiagnostics)
{
    $rejected = @($diagnostics | Where-Object { $_.Line -match $pattern })
    if ($rejected.Count -gt 0)
    {
        throw "Native smoke '$Scenario' emitted supported-path diagnostics:`n$($rejected.Line -join [Environment]::NewLine)"
    }
}

$executable = Join-Path $output $(if ($IsWindows) { 'Orleans.NativeAotSmoke.exe' } else { 'Orleans.NativeAotSmoke' })
if (-not (Test-Path -LiteralPath $executable -PathType Leaf))
{
    throw "Native smoke '$Scenario' publish produced no executable at '$executable'."
}

& $executable *>&1 | Tee-Object -FilePath $runtimeLog
$runtimeExitCode = $LASTEXITCODE
if ($runtimeExitCode -ne 0)
{
    throw "Native smoke '$Scenario' execution failed with exit code $runtimeExitCode. See '$runtimeLog'."
}
