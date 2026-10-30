[CmdletBinding()]
param(
    [string] $DotNetPath = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$targetsPath = Join-Path $PSScriptRoot '..\..\src\Dashboard\Orleans.Dashboard\Orleans.Dashboard.Frontend.targets'
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "orleans frontend tests $([guid]::NewGuid())"
$savedPath = $env:PATH
$savedTestRoot = $env:ORLEANS_DASHBOARD_TEST_ROOT
$nodePath = (Get-Command node -CommandType Application -TotalCount 1).Source
$testsRun = 0

function Assert-Equal {
    param($Expected, $Actual)
    if ($Expected -cne $Actual) {
        throw "Expected '$Expected', actual '$Actual'."
    }
}

function Write-TestFile {
    param([string] $Path, [string] $Content)
    [void] [IO.Directory]::CreateDirectory((Split-Path $Path))
    [IO.File]::WriteAllText($Path, $Content)
}

function Set-ToolState {
    param([hashtable] $Changes)
    foreach ($key in $Changes.Keys) {
        $state[$key] = $Changes[$key]
    }
    Write-TestFile (Join-Path $caseRoot 'tool-state.json') ($state | ConvertTo-Json)
}

function Assert-Commands {
    param([int] $Installs, [int] $Builds)
    $commands = @(Get-Content (Join-Path $caseRoot 'commands.log'))
    Assert-Equal "installs=$Installs;builds=$Builds" "installs=$(@($commands | Where-Object { $_ -eq 'ci' }).Count);builds=$(@($commands | Where-Object { $_ -eq 'run build' }).Count)"
}

function Assert-Discovery {
    param([int] $Builds)
    $expected = @(
        'node --version'
        'npm --version'
        'npm config get userconfig globalconfig omit include ignore-scripts legacy-peer-deps install-links bin-links install-strategy'
    )
    $probes = @(Get-Content (Join-Path $caseRoot 'probes.log') | Where-Object { $_ -notlike 'npm ci *' -and $_ -ne 'npm run build' })
    Assert-Equal (($expected * $Builds) -join '|') ($probes -join '|')
}

function Invoke-Build {
    param(
        [string] $Target = 'Build',
        [string[]] $Properties = @(),
        [switch] $ExpectFailure
    )
    $output = & $DotNetPath msbuild $projectPath "-t:$Target" -m:2 -nr:false -nologo -v:minimal @Properties 2>&1
    $exitCode = $LASTEXITCODE
    Write-Verbose ($output -join "`n")
    if (($exitCode -eq 0) -eq $ExpectFailure.IsPresent) {
        throw "Unexpected MSBuild exit code $exitCode.`n$($output -join "`n")"
    }
}

function Test-Case {
    param([string] $Name, [scriptblock] $Action)
    $script:caseRoot = Join-Path $temporaryRoot $Name
    $script:appRoot = Join-Path $caseRoot 'Orleans.Dashboard.App'
    $projectRoot = Join-Path $caseRoot 'Orleans.Dashboard'
    $script:projectPath = Join-Path $projectRoot 'Dashboard.proj'
    $script:buildMarker = Join-Path $projectRoot 'obj\frontend.build.marker'
    $script:installMarker = Join-Path $appRoot 'node_modules\.orleans.install.marker'
    $script:state = @{
        NodeVersion = 'v24.0.0'
        NpmVersion = '11.0.0'
        Omit = ''
        FailInstall = $false
        FailBuild = $false
        MissingOutput = $false
    }
    $bin = Join-Path $caseRoot 'bin'
    [void] [IO.Directory]::CreateDirectory($bin)
    [void] [IO.Directory]::CreateDirectory($projectRoot)
    Copy-Item $targetsPath (Join-Path $projectRoot 'Frontend.targets')
    Set-ToolState @{}
    Write-TestFile (Join-Path $appRoot 'package.json') '{"name":"test","version":"1.0.0"}'
    Write-TestFile (Join-Path $appRoot 'package-lock.json') '{"lockfileVersion":3}'
    Write-TestFile (Join-Path $appRoot '.npmrc') 'registry=https://example.invalid/'
    Write-TestFile (Join-Path $appRoot 'src\main.ts') 'console.log("dashboard")'
    Write-TestFile (Join-Path $appRoot 'src\removed.ts') 'export const removed = true'
    Write-TestFile (Join-Path $appRoot 'public\image.svg') '<svg/>'
    Write-TestFile (Join-Path $appRoot 'vite.config.ts') 'export default {}'
    Write-TestFile (Join-Path $appRoot 'tsconfig.json') '{}'
    Write-TestFile (Join-Path $appRoot 'index.html') '<html/>'
    Write-TestFile (Join-Path $caseRoot 'user.npmrc') 'fund=false'
    Write-TestFile (Join-Path $caseRoot 'global.npmrc') 'audit=false'

    Write-TestFile $projectPath @'
<Project DefaultTargets="Build">
  <PropertyGroup>
    <BaseIntermediateOutputPath>obj\</BaseIntermediateOutputPath>
    <TargetFrameworks>net8.0;net10.0</TargetFrameworks>
    <_BuildDependsOn Condition="'$(TargetFramework)' == ''">DispatchToInnerBuilds</_BuildDependsOn>
    <_BuildDependsOn Condition="'$(TargetFramework)' != ''">BeforeBuild</_BuildDependsOn>
  </PropertyGroup>
  <Import Project="Frontend.targets" />
  <Target Name="DispatchToInnerBuilds">
    <ItemGroup><_Framework Include="$(TargetFrameworks)" /></ItemGroup>
    <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="Build" Properties="TargetFramework=%(_Framework.Identity)" BuildInParallel="true" />
  </Target>
  <Target Name="BeforeBuild" />
  <Target Name="Build" DependsOnTargets="$(_BuildDependsOn)">
    <WriteLinesToFile File="obj\resources.$(TargetFramework).txt" Lines="@(EmbeddedResource->'%(Link)')" Overwrite="true" Condition="'$(TargetFramework)' != ''" />
  </Target>
  <Target Name="Clean" />
  <Target Name="Rebuild" DependsOnTargets="Clean;Build" />
</Project>
'@

    Write-TestFile (Join-Path $bin 'tool.cjs') @'
const fs = require('node:fs');
const path = require('node:path');
const root = process.env.ORLEANS_DASHBOARD_TEST_ROOT;
const state = JSON.parse(fs.readFileSync(path.join(root, 'tool-state.json'), 'utf8'));
const [tool, ...args] = process.argv.slice(2);
const log = (file, value) => fs.appendFileSync(path.join(root, file), value + '\n');
log('probes.log', tool + ' ' + args.join(' '));
if (tool === 'node') { console.log(state.NodeVersion); process.exit(0); }
switch (args[0]) {
    case '--version': console.log(state.NpmVersion); break;
    case 'config': {
        const config = {
            userconfig: path.join(root, 'user.npmrc'),
            globalconfig: path.join(root, 'global.npmrc'),
            omit: state.Omit,
            include: '',
            'ignore-scripts': false,
            'legacy-peer-deps': false,
            'install-links': false,
            'bin-links': true,
            'install-strategy': process.env.NPM_CONFIG_INSTALL_STRATEGY || 'hoisted',
        };
        for (const key of args.slice(2)) {
            if (!Object.hasOwn(config, key)) throw new Error('Unexpected npm config key: ' + key);
            console.log(key + '=' + config[key]);
        }
        break;
    }
    case 'ci':
        log('commands.log', 'ci');
        fs.mkdirSync('node_modules', { recursive: true });
        if (state.FailInstall) process.exit(17);
        fs.writeFileSync('node_modules/.package-lock.json', '{}');
        break;
    case 'run':
        if (args[1] !== 'build') throw new Error('Unexpected npm command: ' + args);
        log('commands.log', 'run build');
        fs.rmSync('dist', { recursive: true, force: true });
        fs.mkdirSync('dist/fonts', { recursive: true });
        if (state.FailBuild) process.exit(19);
        for (const file of ['index.html', 'index.min.js', 'index.min.js.map', 'fonts/font.woff2']) {
            fs.writeFileSync(path.join('dist', file), 'asset');
        }
        if (!state.MissingOutput) fs.writeFileSync('dist/index.css', 'css');
        fs.cpSync('public', 'dist', { recursive: true, preserveTimestamps: true });
        break;
    default: throw new Error('Unexpected npm command: ' + args);
}
'@
    foreach ($tool in @('node', 'npm')) {
        if ($IsWindows) {
            Write-TestFile (Join-Path $bin "$tool.cmd") "@`"$nodePath`" `"%~dp0tool.cjs`" $tool %*"
        } else {
            $shim = Join-Path $bin $tool
            Write-TestFile $shim "#!/bin/sh`nexec '$nodePath' '$bin/tool.cjs' $tool `"`$@`"`n"
            [IO.File]::SetUnixFileMode($shim, [IO.UnixFileMode] 'UserRead,UserWrite,UserExecute')
        }
    }
    $env:ORLEANS_DASHBOARD_TEST_ROOT = $caseRoot
    $env:PATH = "$bin$([IO.Path]::PathSeparator)$savedPath"
    & $Action
    $script:testsRun++
    Write-Output "PASS $Name"
}

try {
    Test-Case 'UnchangedBuildRunsDiscoveryOnly' {
        Invoke-Build
        Assert-Discovery 1
        Invoke-Build
        Assert-Discovery 2
        Assert-Commands 1 1
    }

    Test-Case 'SourceAndBuildConfigEditsReuseDependencies' {
        Invoke-Build
        $builds = 1
        foreach ($file in @('src\main.ts', 'vite.config.ts', 'tsconfig.json', 'index.html', '.env.production')) {
            Write-TestFile (Join-Path $appRoot $file) "changed $builds"
            Invoke-Build
            Assert-Commands 1 (++$builds)
        }
    }

    Test-Case 'SourceRemovalAndPublicRenameInvalidateBuild' {
        Invoke-Build
        Remove-Item -LiteralPath (Join-Path $appRoot 'src\removed.ts')
        Invoke-Build
        Assert-Commands 1 2
        Move-Item (Join-Path $appRoot 'public\image.svg') (Join-Path $appRoot 'public\renamed.svg')
        Invoke-Build
        Assert-Commands 1 3
        Assert-Equal $false (Test-Path (Join-Path $appRoot 'dist\image.svg'))
        Assert-Equal $true (Test-Path (Join-Path $appRoot 'dist\renamed.svg'))
    }

    Test-Case 'ContentChangesWithRestoredTimestampsInvalidateBuild' {
        Invoke-Build
        $source = Join-Path $appRoot 'src\main.ts'
        $timestamp = [IO.File]::GetLastWriteTimeUtc($source)
        Write-TestFile $source 'changed with original timestamp'
        [IO.File]::SetLastWriteTimeUtc($source, $timestamp)
        Invoke-Build
        Assert-Commands 1 2
    }

    Test-Case 'DependencyAndNpmConfigEditsReinstall' {
        Invoke-Build
        $installs = 1
        foreach ($file in @('Orleans.Dashboard.App\package.json', 'Orleans.Dashboard.App\package-lock.json', 'Orleans.Dashboard.App\.npmrc', 'user.npmrc', 'global.npmrc')) {
            Add-Content (Join-Path $caseRoot $file) 'changed'
            Invoke-Build
            Assert-Commands (++$installs) $installs
        }
        Remove-Item -LiteralPath (Join-Path $appRoot '.npmrc') -Force
        Invoke-Build
        Assert-Commands (++$installs) $installs
        Set-ToolState @{ Omit = 'dev' }
        Invoke-Build
        Assert-Commands (++$installs) $installs
    }

    Test-Case 'NodeAndNpmVersionChangesReinstall' {
        Invoke-Build
        Set-ToolState @{ NodeVersion = 'v26.0.0' }
        Invoke-Build
        Assert-Commands 2 2
        Set-ToolState @{ NpmVersion = '12.0.0' }
        Invoke-Build
        Assert-Commands 3 3
    }

    Test-Case 'InstallStrategyEnvironmentChangeReinstalls' {
        $savedStrategy = $env:NPM_CONFIG_INSTALL_STRATEGY
        try {
            $env:NPM_CONFIG_INSTALL_STRATEGY = 'hoisted'
            Invoke-Build
            Assert-Commands 1 1
            $env:NPM_CONFIG_INSTALL_STRATEGY = 'nested'
            Invoke-Build
            Assert-Commands 2 2
            Invoke-Build
            Assert-Commands 2 2
            Assert-Discovery 3
        } finally {
            $env:NPM_CONFIG_INSTALL_STRATEGY = $savedStrategy
        }
    }

    Test-Case 'MissingDependencyTreeOrStampReinstalls' {
        Invoke-Build
        Remove-Item -LiteralPath (Join-Path $appRoot 'node_modules') -Recurse -Force
        Invoke-Build
        Assert-Commands 2 2
        Remove-Item -LiteralPath $installMarker -Force
        Invoke-Build
        Assert-Commands 3 3
    }

    Test-Case 'MissingDistOrAnyRecordedAssetRebuilds' {
        Invoke-Build
        $builds = 1
        foreach ($file in @('dist', 'dist\index.html', 'dist\index.min.js', 'dist\index.css', 'dist\fonts\font.woff2', 'dist\index.min.js.map')) {
            Remove-Item -LiteralPath (Join-Path $appRoot $file) -Recurse -Force
            Invoke-Build
            Assert-Commands 1 (++$builds)
        }
    }

    Test-Case 'InstallFailureInvalidatesSuccessAndRetries' {
        Invoke-Build
        Add-Content (Join-Path $appRoot 'package-lock.json') 'changed'
        Set-ToolState @{ FailInstall = $true }
        Invoke-Build -ExpectFailure
        Assert-Commands 2 1
        Assert-Equal $false (Test-Path $installMarker)
        Assert-Equal $false (Test-Path $buildMarker)
        Set-ToolState @{ FailInstall = $false }
        Invoke-Build
        Assert-Commands 3 2
    }

    Test-Case 'BuildFailureInvalidatesSuccessAndReusesDependencies' {
        Invoke-Build
        Add-Content (Join-Path $appRoot 'src\main.ts') 'changed'
        Set-ToolState @{ FailBuild = $true }
        Invoke-Build -ExpectFailure
        Assert-Commands 1 2
        Assert-Equal $true (Test-Path $installMarker)
        Assert-Equal $false (Test-Path $buildMarker)
        Set-ToolState @{ FailBuild = $false }
        Invoke-Build
        Assert-Commands 1 3
    }

    Test-Case 'MissingRequiredOutputFailsWithoutSuccessStamp' {
        Set-ToolState @{ MissingOutput = $true }
        Invoke-Build -ExpectFailure
        Assert-Equal $false (Test-Path $buildMarker)
        Set-ToolState @{ MissingOutput = $false }
        Invoke-Build
        Assert-Commands 1 2
    }

    Test-Case 'CleanAndRebuildPreserveDependencies' {
        Invoke-Build
        Invoke-Build -Target Clean
        Assert-Equal $false (Test-Path $buildMarker)
        Assert-Equal $true (Test-Path $installMarker)
        Invoke-Build
        Assert-Commands 1 2
        Invoke-Build -Target Rebuild
        Assert-Commands 1 3
        Remove-Item -LiteralPath (Join-Path $caseRoot 'Orleans.Dashboard\obj') -Recurse -Force
        Invoke-Build
        Assert-Commands 1 4
    }

    Test-Case 'MultiFrameworkBuildOwnsAssetsOnceAndEmbedsAllFiles' {
        Invoke-Build
        Assert-Commands 1 1
        Assert-Discovery 1
        $expected = @('wwwroot/fonts/font.woff2', 'wwwroot/image.svg', 'wwwroot/index.css', 'wwwroot/index.html', 'wwwroot/index.min.js', 'wwwroot/index.min.js.map')
        foreach ($framework in @('net8.0', 'net10.0')) {
            $resources = @(Get-Content (Join-Path $caseRoot "Orleans.Dashboard\obj\resources.$framework.txt"))
            Assert-Equal ($expected -join '|') (($resources.Replace('\', '/') | Sort-Object) -join '|')
        }
    }

    Test-Case 'SingleFrameworkBuildUsesOuterAssetOwner' {
        Invoke-Build -Properties '-p:TargetFramework=net8.0'
        Invoke-Build -Properties '-p:TargetFramework=net10.0'
        Assert-Commands 1 1
        Assert-Discovery 2
    }

    Test-Case 'DisabledExternalAssetsRunNoTools' {
        Invoke-Build -Properties '-p:BuildExternalAssets=false'
        Assert-Equal $false (Test-Path (Join-Path $caseRoot 'probes.log'))
        Assert-Equal $false (Test-Path $installMarker)
        Assert-Equal $false (Test-Path $buildMarker)
    }

    Write-Output "Passed $testsRun Dashboard frontend build tests."
} finally {
    $env:PATH = $savedPath
    $env:ORLEANS_DASHBOARD_TEST_ROOT = $savedTestRoot
    if (Test-Path $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
