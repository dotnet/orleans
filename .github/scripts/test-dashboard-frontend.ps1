[CmdletBinding()]
param(
    [string] $DotNetPath = 'dotnet'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repository = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "orleans frontend tests $([guid]::NewGuid())"
$savedPath = $env:PATH
$testsRun = 0

function Write-TestFile {
    param([string] $Path, [string] $Content)
    [void] [IO.Directory]::CreateDirectory((Split-Path $Path))
    [IO.File]::WriteAllText($Path, $Content)
}

function Write-Manifest {
    param([string] $Name, [string] $Root, [string[]] $Files, [switch] $NormalizeText)
    [string[]] $lines = foreach ($file in $Files) {
        $bytes = [IO.File]::ReadAllBytes($file)
        if ($NormalizeText) {
            $bytes = [Text.Encoding]::UTF8.GetBytes([Text.Encoding]::UTF8.GetString($bytes).Replace("`r`n", "`n"))
        }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes))
        "$hash  $([IO.Path]::GetRelativePath($Root, $file).Replace('\', '/'))"
    }
    [Array]::Sort($lines, [StringComparer]::Ordinal)
    [IO.File]::WriteAllLines((Join-Path $appRoot $Name), $lines)
}

function Invoke-Build {
    param([string] $Target = 'Build', [string[]] $Properties = @(), [switch] $ExpectStale)
    $output = & $DotNetPath msbuild $projectPath "-t:$Target" -m:2 -nr:false -nologo -v:minimal @Properties 2>&1
    $exitCode = $LASTEXITCODE
    if ($ExpectStale) {
        if ($exitCode -eq 0 -or ($output -join "`n") -notmatch 'ORLEANSDASHBOARD001.*npm run assets:generate') {
            throw "Expected actionable stale-assets failure, got exit $exitCode.`n$($output -join "`n")"
        }
    } elseif ($exitCode -ne 0) {
        throw "Unexpected MSBuild exit $exitCode.`n$($output -join "`n")"
    }
}

function Test-Case {
    param([string] $Name, [scriptblock] $Action)
    $caseRoot = Join-Path $temporaryRoot $Name
    $script:appRoot = Join-Path $caseRoot 'Orleans.Dashboard.App'
    $projectRoot = Join-Path $caseRoot 'Orleans.Dashboard'
    $script:projectPath = Join-Path $projectRoot 'Dashboard.proj'
    $script:outputRoot = Join-Path $appRoot 'dist'
    [void] [IO.Directory]::CreateDirectory($projectRoot)
    Copy-Item (Join-Path $repository 'src\Dashboard\Orleans.Dashboard\Orleans.Dashboard.Frontend.targets') (Join-Path $projectRoot 'Frontend.targets')
    Write-TestFile (Join-Path $appRoot 'src\main.ts') "console.log('dashboard');`n"
    Write-TestFile (Join-Path $appRoot 'public\image.svg') "<svg/>`n"
    Write-TestFile (Join-Path $appRoot 'package.json') "{}`n"
    Write-TestFile (Join-Path $appRoot 'package-lock.json') "{}`n"
    Write-TestFile (Join-Path $appRoot 'vite.config.ts') "export default {}`n"
    Write-TestFile (Join-Path $appRoot 'generate-assets.mjs') "// generator`n"
    Write-TestFile (Join-Path $appRoot 'scripts\install.mjs') "// installer`n"
    Write-TestFile (Join-Path $appRoot '.node-version') "24.22.2`n"
    Write-TestFile (Join-Path $appRoot '.npmrc') "registry=https://example.invalid/`n"
    Write-TestFile (Join-Path $appRoot 'index.html') "<html/>`n"
    Copy-Item (Join-Path $repository 'src\Dashboard\Orleans.Dashboard.App\verify-assets.cs') (Join-Path $appRoot 'verify-assets.cs')
    $inputs = @(Get-ChildItem $appRoot -File -Recurse -Force | ForEach-Object FullName)
    Write-Manifest 'assets.inputs.sha256' $appRoot $inputs -NormalizeText
    foreach ($asset in @('index.html', 'index.min.js', 'index.min.js.map', 'index.css', 'img\image.png', 'fonts\font.woff2', 'THIRD-PARTY-NOTICES.txt')) {
        Write-TestFile (Join-Path $outputRoot $asset) "asset $asset`n"
    }
    Write-Manifest 'assets.outputs.sha256' $outputRoot @(Get-ChildItem $outputRoot -File -Recurse -Force | ForEach-Object FullName)
    Write-TestFile $projectPath @'
<Project DefaultTargets="Build">
  <PropertyGroup>
    <_BuildDependsOn Condition="'$(TargetFramework)' == ''">DispatchToInnerBuilds</_BuildDependsOn>
    <_BuildDependsOn Condition="'$(TargetFramework)' != ''">BeforeBuild</_BuildDependsOn>
  </PropertyGroup>
  <Import Project="Frontend.targets" />
  <Target Name="DispatchToInnerBuilds">
    <ItemGroup><_Framework Include="net8.0;net10.0" /></ItemGroup>
    <MSBuild Projects="$(MSBuildProjectFullPath)" Targets="Build" Properties="TargetFramework=%(_Framework.Identity)" BuildInParallel="true" />
  </Target>
  <Target Name="BeforeBuild" />
  <Target Name="Build" DependsOnTargets="$(_BuildDependsOn)">
    <WriteLinesToFile File="resources.$(TargetFramework).txt" Lines="@(EmbeddedResource->'%(Link)')" Overwrite="true" Condition="'$(TargetFramework)' != ''" />
  </Target>
  <Target Name="GetTargetFrameworks" />
  <Target Name="Clean" />
  <Target Name="Rebuild" DependsOnTargets="Clean;Build" />
  <Target Name="GenerateNuspec" />
  <Target Name="Pack" DependsOnTargets="GenerateNuspec" />
</Project>
'@
    & $Action
    $script:testsRun++
    Write-Output "PASS $Name"
}

try {
    $bin = Join-Path $temporaryRoot 'bin'
    $toolLog = Join-Path $temporaryRoot 'tools.log'
    foreach ($tool in @('node', 'npm')) {
        if ($IsWindows) {
            Write-TestFile (Join-Path $bin "$tool.cmd") "@echo unexpected $tool >> `"$toolLog`"`r`n@exit /b 99"
        } else {
            $shim = Join-Path $bin $tool
            Write-TestFile $shim "#!/bin/sh`necho unexpected $tool >> '$toolLog'`nexit 99`n"
            [IO.File]::SetUnixFileMode($shim, [IO.UnixFileMode] 'UserRead,UserWrite,UserExecute')
        }
    }
    $env:PATH = "$bin$([IO.Path]::PathSeparator)$savedPath"

    Test-Case 'NativeSdkResolvesInlineTaskSourceAndRejectsStaleAssets' {
        Invoke-Build -Target VerifyFrontendAssets
        [IO.File]::AppendAllText((Join-Path $appRoot 'src\main.ts'), '// changed')
        Invoke-Build -Target VerifyFrontendAssets -ExpectStale
    }

    Test-Case 'ParallelFrameworkBuildsEmbedCompleteVerifiedOutputs' {
        Invoke-Build
        $expected = @('wwwroot/THIRD-PARTY-NOTICES.txt', 'wwwroot/fonts/font.woff2', 'wwwroot/img/image.png', 'wwwroot/index.css', 'wwwroot/index.html', 'wwwroot/index.min.js', 'wwwroot/index.min.js.map') | Sort-Object
        foreach ($framework in @('net8.0', 'net10.0')) {
            $resources = @([IO.File]::ReadAllLines((Join-Path (Split-Path $projectPath) "resources.$framework.txt"))).Replace('\', '/') | Sort-Object
            if (($resources -join '|') -cne ($expected -join '|')) { throw "Incomplete resource set for $framework." }
        }
        Invoke-Build -Properties '-p:TargetFramework=net8.0'
        Invoke-Build -Properties '-p:TargetFramework=net10.0'
    }

    foreach ($file in @('src\main.ts', 'public\image.svg', 'package.json', 'package-lock.json', '.npmrc', '.node-version', 'vite.config.ts', 'generate-assets.mjs', 'scripts\install.mjs', 'verify-assets.cs', 'index.html')) {
        Test-Case "ChangedInput-$($file.Replace('\', '-'))" {
            $path = Join-Path $appRoot $file
            $timestamp = [IO.File]::GetLastWriteTimeUtc($path)
            [IO.File]::AppendAllText($path, "`n// changed")
            [IO.File]::SetLastWriteTimeUtc($path, $timestamp)
            Invoke-Build -ExpectStale
        }
    }

    Test-Case 'AddedRemovedAndRenamedInputsInvalidate' {
        Write-TestFile (Join-Path $appRoot 'src\added.ts') 'added'
        Invoke-Build -ExpectStale
        Remove-Item -LiteralPath (Join-Path $appRoot 'src\added.ts')
        Remove-Item -LiteralPath (Join-Path $appRoot 'src\main.ts')
        Invoke-Build -ExpectStale
        Write-TestFile (Join-Path $appRoot 'src\renamed.ts') "console.log('dashboard');`n"
        Invoke-Build -ExpectStale
    }

    Test-Case 'NewRootConfigurationInvalidates' {
        Write-TestFile (Join-Path $appRoot '.env.production') 'VITE_EXAMPLE=1'
        Invoke-Build -ExpectStale
    }

    Test-Case 'CaseOnlyRenameInvalidates' {
        Rename-Item -LiteralPath (Join-Path $appRoot 'src\main.ts') -NewName 'temporary.ts'
        Rename-Item -LiteralPath (Join-Path $appRoot 'src\temporary.ts') -NewName 'MAIN.ts'
        Invoke-Build -ExpectStale
    }

    Test-Case 'SourceLineEndingsAreCanonical' {
        foreach ($path in @(Get-ChildItem $appRoot -File -Recurse -Force | Where-Object FullName -NotLike "$outputRoot*" | Where-Object Name -NotLike 'assets.*.sha256' | ForEach-Object FullName)) {
            [IO.File]::WriteAllText($path, [IO.File]::ReadAllText($path).Replace("`r`n", "`n").Replace("`n", "`r`n"))
        }
        Invoke-Build
    }

    Test-Case 'BinarySourceBytesArePreserved' {
        $path = Join-Path $appRoot 'public\binary.png'
        [IO.File]::WriteAllBytes($path, [byte[]] (0, 13, 10, 255))
        $lines = @([IO.File]::ReadAllLines((Join-Path $appRoot 'assets.inputs.sha256')))
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([IO.File]::ReadAllBytes($path)))
        [string[]] $lines = $lines + "$hash  public/binary.png"
        [Array]::Sort($lines, [StringComparer]::Ordinal)
        [IO.File]::WriteAllLines((Join-Path $appRoot 'assets.inputs.sha256'), $lines)
        Invoke-Build
        [IO.File]::WriteAllBytes($path, [byte[]] (0, 10, 255))
        Invoke-Build -ExpectStale
    }

    Test-Case 'OutputContentAndCompletePathSetAreVerified' {
        $path = Join-Path $outputRoot 'index.min.js.map'
        $original = [IO.File]::ReadAllText($path)
        [IO.File]::AppendAllText($path, 'tampered')
        Invoke-Build -ExpectStale
        [IO.File]::WriteAllText($path, $original)
        Remove-Item -LiteralPath $path
        Invoke-Build -ExpectStale
        Write-TestFile (Join-Path $outputRoot 'renamed.map') $original
        Invoke-Build -ExpectStale
        Remove-Item -LiteralPath (Join-Path $outputRoot 'renamed.map')
        Write-TestFile $path $original
        Write-TestFile (Join-Path $outputRoot 'unexpected.js') 'extra'
        Invoke-Build -ExpectStale
    }

    Test-Case 'OutputLineEndingsAreByteExact' {
        $path = Join-Path $outputRoot 'index.min.js'
        [IO.File]::WriteAllText($path, [IO.File]::ReadAllText($path).Replace("`n", "`r`n"))
        Invoke-Build -ExpectStale
    }

    Test-Case 'MissingAndEmptyManifestsFail' {
        $path = Join-Path $appRoot 'assets.inputs.sha256'
        $original = [IO.File]::ReadAllText($path)
        Remove-Item -LiteralPath $path
        Invoke-Build -ExpectStale
        [IO.File]::WriteAllText($path, '')
        Invoke-Build -ExpectStale
        [IO.File]::WriteAllText($path, $original)
        Remove-Item -LiteralPath (Join-Path $appRoot 'assets.outputs.sha256')
        Invoke-Build -ExpectStale
    }

    Test-Case 'DuplicateManifestEntriesFail' {
        $path = Join-Path $appRoot 'assets.outputs.sha256'
        [IO.File]::AppendAllText($path, [IO.File]::ReadAllLines($path)[0] + "`n")
        Invoke-Build -ExpectStale
    }

    Test-Case 'DiscoveryAndCleanAreIndependentOfFrontendState' {
        Remove-Item -LiteralPath (Join-Path $appRoot 'assets.inputs.sha256')
        Invoke-Build -Target GetTargetFrameworks
        Invoke-Build -Target Clean
        if (-not (Test-Path (Join-Path $outputRoot 'index.min.js'))) { throw 'Clean removed committed assets.' }
        Invoke-Build -ExpectStale
    }

    Test-Case 'RebuildAndPackVerifyAssets' {
        Invoke-Build -Target Rebuild
        Invoke-Build -Target Pack
        [IO.File]::AppendAllText((Join-Path $outputRoot 'fonts\font.woff2'), 'tampered')
        Invoke-Build -Target Rebuild -ExpectStale
        Invoke-Build -Target Pack -ExpectStale
    }

    Test-Case 'ExplicitExternalAssetsOptOutIsPreserved' {
        Remove-Item -LiteralPath (Join-Path $appRoot 'assets.inputs.sha256')
        Invoke-Build -Properties '-p:BuildExternalAssets=false'
        Invoke-Build -Target Pack -Properties '-p:BuildExternalAssets=false'
    }

    Test-Case 'RepeatedBuildCleanAndRebuildPreserveAllAssetBytes' {
        $before = [IO.File]::ReadAllText((Join-Path $appRoot 'assets.outputs.sha256'))
        Invoke-Build
        Invoke-Build
        Invoke-Build -Target Clean
        Invoke-Build -Target Rebuild
        $actual = @(Get-ChildItem $outputRoot -File -Recurse -Force | ForEach-Object FullName)
        Write-Manifest 'assets.outputs.sha256' $outputRoot $actual
        if ([IO.File]::ReadAllText((Join-Path $appRoot 'assets.outputs.sha256')) -cne $before) {
            throw 'Build/Clean/Rebuild changed the complete asset path set or bytes.'
        }
    }

    Test-Case 'StaleSourceAndOutputRecoverySucceedsAfterExactRestoration' {
        foreach ($path in @((Join-Path $appRoot 'src\main.ts'), (Join-Path $outputRoot 'index.min.js'))) {
            $original = [IO.File]::ReadAllBytes($path)
            [IO.File]::AppendAllText($path, '// changed')
            Invoke-Build -ExpectStale
            Invoke-Build -ExpectStale
            [IO.File]::WriteAllBytes($path, $original)
            Invoke-Build
        }
    }

    if (Test-Path $toolLog) { throw "A .NET asset operation invoked Node/npm: $([IO.File]::ReadAllText($toolLog))" }
    Write-Output "Passed $testsRun Dashboard frontend asset tests; Node/npm invocations: 0."
} finally {
    $env:PATH = $savedPath
    if (Test-Path $temporaryRoot) { Remove-Item -LiteralPath $temporaryRoot -Recurse -Force }
}
