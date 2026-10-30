[CmdletBinding()]
param(
    [string] $DotNetPath = 'dotnet',
    [string] $Configuration = 'Debug',
    [string[]] $Frameworks = @('net8.0', 'net10.0')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$repositoryRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "orleans-versioned-grain-build-$([guid]::NewGuid())"
$projects = @('TestVersionGrains', 'TestVersionGrains2')
$payload = Join-Path $temporaryRoot 'deployment-input.txt'
$targets = Join-Path $temporaryRoot 'DeploymentTests.targets'
$appHostName = if ($IsWindows) { 'TestVersionGrains.exe' } else { 'TestVersionGrains' }

function Build-VersionedGrains {
    param([string] $Framework, [string] $PayloadTarget = 'deployment\input.txt', [string] $ServerGC = 'true')

    foreach ($project in $projects) {
        & $DotNetPath build (Join-Path $repositoryRoot "test\Grains\$project\$project.csproj") `
            --framework $Framework --configuration $Configuration --verbosity quiet --nologo `
            '-m:1' '-nodeReuse:false' '-p:BuildInParallel=false' `
            "-p:CustomAfterMicrosoftCommonTargets=$targets" `
            "-p:DeploymentTestPayload=$payload" "-p:DeploymentTestTargetPath=$PayloadTarget" `
            "-p:ServerGarbageCollection=$ServerGC"
        if ($LASTEXITCODE -ne 0) {
            throw "$project ($Framework) build failed with exit code $LASTEXITCODE."
        }
    }
}

function Get-DeploymentDirectory {
    param([string] $Project, [string] $Framework)

    return Join-Path $repositoryRoot "test\Grains\$Project\bin\$Configuration\publish\$Framework"
}

function Get-DeploymentSnapshot {
    param([string] $Project, [string] $Framework)

    $directory = Get-DeploymentDirectory $Project $Framework
    $fileList = Join-Path $repositoryRoot "test\Grains\$Project\obj\$Configuration\$Framework\$Project.csproj.FileListAbsolute.txt"
    $prefix = "$directory$([IO.Path]::DirectorySeparatorChar)"
    $paths = Get-Content -LiteralPath $fileList |
        Where-Object { $_.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) } |
        Sort-Object -Unique
    $snapshot = @{}
    foreach ($path in $paths) {
        $file = Get-Item -LiteralPath $path
        $relativePath = [IO.Path]::GetRelativePath($directory, $file.FullName)
        $snapshot[$relativePath] = @{
            Hash = (Get-FileHash -LiteralPath $file.FullName).Hash
            Timestamp = $file.LastWriteTimeUtc
        }
    }

    return $snapshot
}

function Assert-DeploymentSnapshot {
    param([string] $Project, [string] $Framework, [hashtable] $Expected, [switch] $CheckTimestamps)

    $directory = Get-DeploymentDirectory $Project $Framework
    $actual = Get-DeploymentSnapshot $Project $Framework
    if ($actual.Count -ne $Expected.Count) {
        throw "Deployment file count changed in '$directory': expected $($Expected.Count), got $($actual.Count)."
    }

    foreach ($path in $Expected.Keys) {
        if (-not $actual.ContainsKey($path) -or $actual[$path].Hash -ne $Expected[$path].Hash) {
            throw "Deployment file '$path' in '$directory' differs from the expected content."
        }

        if ($CheckTimestamps -and $actual[$path].Timestamp -ne $Expected[$path].Timestamp) {
            throw "Unchanged build rewrote '$path' in '$directory'."
        }
    }
}

try {
    [void] (New-Item -ItemType Directory -Path $temporaryRoot)
    # Fail on a second deployment traversal, including an otherwise incremental Publish.
    @'
<Project>
  <ItemGroup Condition="'$(MSBuildProjectName)' == 'TestGrains'">
    <None Include="$(DeploymentTestPayload)" TargetPath="$(DeploymentTestTargetPath)"
          CopyToOutputDirectory="PreserveNewest" CopyToPublishDirectory="PreserveNewest" />
  </ItemGroup>
  <Target Name="RejectVersionedGrainPublish" BeforeTargets="Publish">
    <Error Text="Versioned grain Build must prepare its deployment without invoking Publish." />
  </Target>
</Project>
'@ | Set-Content -LiteralPath $targets

    foreach ($framework in $Frameworks) {
        Set-Content -LiteralPath $payload -Value 'initial deployment content'
        Build-VersionedGrains $framework
        $snapshots = @{}
        $identities = @()
        $grainHashes = @()
        foreach ($project in $projects) {
            $directory = Get-DeploymentDirectory $project $framework
            $snapshots[$project] = Get-DeploymentSnapshot $project $framework
            foreach ($required in @($appHostName, 'TestVersionGrains.dll', 'TestVersionGrains.deps.json',
                    'TestVersionGrains.runtimeconfig.json', 'TestGrains.dll', 'Orleans.Runtime.dll', 'deployment\input.txt')) {
                if (-not (Test-Path -LiteralPath (Join-Path $directory $required))) {
                    throw "Missing runtime asset '$required' in '$directory'."
                }
            }
            $assemblyPath = Join-Path $directory 'TestVersionGrains.dll'
            $identities += [Reflection.AssemblyName]::GetAssemblyName($assemblyPath).FullName
            $grainHashes += $snapshots[$project]['TestVersionGrains.dll'].Hash
            $runtimeConfig = Get-Content -LiteralPath (Join-Path $directory 'TestVersionGrains.runtimeconfig.json') -Raw | ConvertFrom-Json
            if ($runtimeConfig.runtimeOptions.tfm -ne $framework) {
                throw "Deployment '$directory' targets the wrong framework."
            }
        }
        if ($identities[0] -ne $identities[1] -or $grainHashes[0] -eq $grainHashes[1]) {
            throw 'The two grain versions must have the same assembly identity and distinct implementations.'
        }
        Write-Output "PASS InitialDeployment ($framework): isolated v1/v2 implementations have the same assembly identity and complete runtime assets."

        Build-VersionedGrains $framework
        foreach ($project in $projects) {
            Assert-DeploymentSnapshot $project $framework $snapshots[$project] -CheckTimestamps
        }
        Write-Output "PASS UnchangedBuild ($framework): zero Publish invocations and zero deployment file rewrites."

        # Keep the main DLL: a single-assembly freshness marker would miss these deletions.
        foreach ($project in $projects) {
            $directory = Get-DeploymentDirectory $project $framework
            foreach ($path in $snapshots[$project].Keys) {
                if ($path -ne 'TestVersionGrains.dll') {
                    Remove-Item -LiteralPath (Join-Path $directory $path)
                }
            }
        }
        Build-VersionedGrains $framework
        foreach ($project in $projects) {
            Assert-DeploymentSnapshot $project $framework $snapshots[$project]
        }
        Write-Output "PASS DeletedRuntimeAssets ($framework): every dependency, apphost, config and content file is restored."

        Set-Content -LiteralPath $payload -Value 'updated deployment content with a different length'
        Build-VersionedGrains $framework -ServerGC false
        foreach ($project in $projects) {
            $directory = Get-DeploymentDirectory $project $framework
            $content = Get-Content -LiteralPath (Join-Path $directory 'deployment\input.txt') -Raw
            if ($content.Trim() -ne 'updated deployment content with a different length') {
                throw "Changed content was not deployed to '$directory'."
            }
            $runtimeConfig = Get-Content -LiteralPath (Join-Path $directory 'TestVersionGrains.runtimeconfig.json') -Raw | ConvertFrom-Json
            if ($runtimeConfig.runtimeOptions.configProperties.'System.GC.Server' -ne $false) {
                throw "Changed runtime configuration was not deployed to '$directory'."
            }
        }
        Write-Output "PASS ChangedInputs ($framework): dependency content and runtime configuration are refreshed."

        Build-VersionedGrains $framework -PayloadTarget 'deployment\renamed.txt'
        foreach ($project in $projects) {
            $directory = Get-DeploymentDirectory $project $framework
            if ((Test-Path -LiteralPath (Join-Path $directory 'deployment\input.txt')) -or
                -not (Test-Path -LiteralPath (Join-Path $directory 'deployment\renamed.txt'))) {
                throw "Renamed content was not reconciled in '$directory'."
            }
            $runtimeConfig = Get-Content -LiteralPath (Join-Path $directory 'TestVersionGrains.runtimeconfig.json') -Raw | ConvertFrom-Json
            if ($runtimeConfig.runtimeOptions.configProperties.'System.GC.Server' -ne $true) {
                throw "Restored runtime configuration was not deployed to '$directory'."
            }
        }
        Write-Output "PASS RenamedContent ($framework): the deployment contains the current content path."
    }
}
finally {
    Remove-Item -LiteralPath $payload, $targets -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath $temporaryRoot -ErrorAction SilentlyContinue
}
