[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $PackageDirectory,
    [string] $PackageVersion,
    [string[]] $Frameworks = @('net8.0', 'net10.0'),
    [string[]] $Consumers = @(),
    [switch] $SkipPack
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
$env:MSBUILDDISABLENODEREUSE = '1'
$repositoryRoot = Split-Path $PSScriptRoot -Parent
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) "orleans-package-tests-$([guid]::NewGuid())"
$serializationProject = Join-Path $repositoryRoot 'src/Orleans.Serialization/Orleans.Serialization.csproj'

function Invoke-DotNet {
    & dotnet @args
    if ($LASTEXITCODE -ne 0) {
        throw "dotnet $args failed with exit code $LASTEXITCODE."
    }
}

if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot "Artifacts/$Configuration"
}

$PackageDirectory = [IO.Path]::GetFullPath($PackageDirectory)
if ([string]::IsNullOrWhiteSpace($PackageVersion)) {
    $PackageVersion = & dotnet msbuild $serializationProject "-p:Configuration=$Configuration" -getProperty:PackageVersion -nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Could not determine the package version.'
    }
}

$packages = @(
    'Serialization',
    'Serialization.NewtonsoftJson',
    'Serialization.SystemTextJson',
    'Serialization.MessagePack',
    'Serialization.FSharp',
    'Serialization.Protobuf',
    'Core.Abstractions',
    'Core',
    'Runtime',
    'Streaming',
    'Sdk',
    'Client',
    'Server'
)

if (-not $SkipPack) {
    $projects = @(
        'Orleans.Analyzers.Contracts',
        'Orleans.CodeGenerator',
        'Orleans.Serialization.Abstractions',
        'Orleans.Persistence.Memory'
    ) + @($packages | ForEach-Object { "Orleans.$_" })
    foreach ($project in $projects) {
        $relativePath = if ($project -eq 'Orleans.Serialization.Protobuf') {
            "src/Serializers/$project/$project.csproj"
        } else {
            "src/$project/$project.csproj"
        }

        Invoke-DotNet pack (Join-Path $repositoryRoot $relativePath) --configuration $Configuration `
            "-p:PackageVersion=$PackageVersion" --output $PackageDirectory --verbosity quiet '-m:1' '-p:BuildInParallel=false'
    }
}

try {
    [void] (New-Item -ItemType Directory -Path $temporaryRoot)
    # Keep consumer builds outside the repository so its codegen imports cannot mask missing package assets.
    $source = @'
using Microsoft.Extensions.DependencyInjection;
using Orleans;
using Orleans.Serialization;

using var services = new ServiceCollection().AddSerializer().BuildServiceProvider();
var serializer = services.GetRequiredService<Serializer>();
var original = new Greeting { Text = "Hello", Number = 42 };
var result = serializer.Deserialize<Greeting>(serializer.SerializeToArray(original));
if (result is null || ReferenceEquals(original, result) || result.Text != "Hello" || result.Number != 42)
{
    throw new InvalidOperationException("The generated codec did not round-trip the payload.");
}

var copy = services.GetRequiredService<DeepCopier>().Copy(original);
if (copy is null || ReferenceEquals(original, copy) || copy.Text != "Hello" || copy.Number != 42)
{
    throw new InvalidOperationException("The generated copier did not copy the payload.");
}

Console.WriteLine("Generated codec and copier passed.");

[GenerateSerializer]
public sealed class Greeting
{
    [Id(0)]
    public string Text { get; set; } = "";

    [Id(1)]
    public int Number { get; set; }
}
'@
    $packageSource = [System.Security.SecurityElement]::Escape($PackageDirectory)
    @"
<configuration>
  <packageSources>
    <clear />
    <add key="local" value="$packageSource" />
    <add key="nuget.org" value="https://packagefeedproxy.microsoft.io/nuget/v3/index.json" />
  </packageSources>
  <packageSourceMapping>
    <clear />
    <packageSource key="local"><package pattern="Microsoft.Orleans.*" /></packageSource>
    <packageSource key="nuget.org"><package pattern="*" /></packageSource>
  </packageSourceMapping>
</configuration>
"@ | Set-Content -LiteralPath (Join-Path $temporaryRoot 'NuGet.Config')

    $cases = @($packages | ForEach-Object { @{ Name = $_; Packages = @($_) } })
    $cases += @{ Name = 'Combined'; Packages = @('Serialization', 'Sdk', 'Client', 'Server') }
    if ($Consumers.Count -gt 0) {
        foreach ($consumer in $Consumers) {
            if ($consumer -notin $cases.Name) {
                throw "Unknown package consumer '$consumer'."
            }
        }

        $cases = @($cases | Where-Object { $_.Name -in $Consumers })
    }

    $passed = 0
    foreach ($case in $cases) {
        $caseRoot = Join-Path $temporaryRoot $case.Name
        [void] (New-Item -ItemType Directory -Path $caseRoot)
        $references = ($case.Packages | ForEach-Object {
            "    <PackageReference Include=`"Microsoft.Orleans.$_`" Version=`"$PackageVersion`" />"
        }) -join [Environment]::NewLine
        $project = Join-Path $caseRoot 'Consumer.csproj'
        @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFrameworks>$($Frameworks -join ';')</TargetFrameworks>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <TreatWarningsAsErrors>true</TreatWarningsAsErrors>
  </PropertyGroup>
  <ItemGroup>
$references
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="8.0.1" Condition="'`$(TargetFramework)' != 'net10.0'" />
    <PackageReference Include="Microsoft.Extensions.DependencyInjection" Version="10.0.5" Condition="'`$(TargetFramework)' == 'net10.0'" />
  </ItemGroup>
  <Target Name="VerifyOrleansAnalyzers" BeforeTargets="CoreCompile">
    <ItemGroup>
      <_Generator Include="@(Analyzer)" Condition="'%(Filename)' == 'Orleans.CodeGenerator'" />
      <_Analyzer Include="@(Analyzer)" Condition="'%(Filename)' == 'Orleans.Analyzers'" />
    </ItemGroup>
    <Error Condition="'@(_Generator->Count())' != '1'" Text="Expected exactly one Orleans generator: @(_Generator)" />
    <Error Condition="'@(_Analyzer->Count())' != '1'" Text="Expected exactly one Orleans analyzer: @(_Analyzer)" />
  </Target>
  <Target Name="VerifyAnalyzerOutputs" AfterTargets="Build">
    <Error Condition="Exists('`$(TargetDir)Orleans.CodeGenerator.dll') or Exists('`$(TargetDir)Orleans.Analyzers.dll') or Exists('`$(TargetDir)Orleans.Analyzers.Contracts.dll')"
           Text="Analyzer assemblies must remain build-only assets." />
  </Target>
</Project>
"@ | Set-Content -LiteralPath $project
        $source | Set-Content -LiteralPath (Join-Path $caseRoot 'Program.cs')
        # A fresh cache ensures restore uses these local packages, even if a published version has the same number.
        Invoke-DotNet restore $project --packages (Join-Path $temporaryRoot 'nuget') --verbosity quiet
        foreach ($framework in $Frameworks) {
            Write-Host "Running package consumer $($case.Name) ($framework)"
            Invoke-DotNet build $project --framework $framework --configuration Release --no-restore --verbosity quiet `
                '-m:1' '-p:BuildInParallel=false'
            Invoke-DotNet run --project $project --framework $framework --configuration Release --no-build --no-restore
            $passed++
        }
    }

    Write-Host "Package consumers: Passed: $passed, Failed: 0, Skipped: 0"
} finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
