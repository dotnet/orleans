// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;

namespace PackageJsonGenerator;

internal static class RootGenerateCommand
{
    private static readonly Option<string> s_inputOption = new("--input")
    {
        Required = true,
        Description = "The assembly to generate a Package.{version}.json file for.",
    };

    private static readonly Option<string[]> s_referencesOption = new("--reference")
    {
        AllowMultipleArgumentsPerToken = true,
        Required = true,
        Description = "The assemblies referenced by the input assembly.",
    };

    private static readonly Option<string> s_outputOption = new("--output")
    {
        Required = true,
        Description = "The FilePath to generate a Package.{version}.json file for.",
    };

    private static readonly Option<string?> s_versionOption = new("--package-version")
    {
        Description = "The NuGet package version to use instead of the assembly version.",
    };

    private static readonly Option<string?> s_packageNameOption = new("--package-name")
    {
        Description = "The NuGet package ID to use instead of the assembly name.",
    };

    private static readonly Option<string?> s_sourceRepoOption = new("--source-repo")
    {
        Description = "The source repository URL (e.g. https://github.com/dotnet/orleans). Falls back to assembly RepositoryUrl metadata.",
    };

    private static readonly Option<string?> s_sourceCommitOption = new("--source-commit")
    {
        Description = "The source commit SHA. Falls back to assembly RepositoryCommit metadata.",
    };

    private static readonly Option<string?> s_sourceRootOption = new("--source-root")
    {
        Description = "The repository root used to resolve source files from portable PDB paths.",
    };

    private static readonly Option<string?> s_sourceFileManifestOption = new("--source-file-manifest")
    {
        Description = "A newline-delimited list of repository-relative source files which may be emitted.",
    };

    private static readonly Option<string?> s_targetFrameworkOption = new("--target-framework")
    {
        Description = "The target framework moniker for the analyzed lib folder. Written into package metadata.",
    };

    public static RootCommand GetCommand()
    {
        var formatCommand = new RootCommand("Generates Package.{version}.json files.")
        {
            s_inputOption,
            s_referencesOption,
            s_outputOption,
            s_versionOption,
            s_packageNameOption,
            s_sourceRepoOption,
            s_sourceCommitOption,
            s_sourceRootOption,
            s_sourceFileManifestOption,
            s_targetFrameworkOption,
        };

        formatCommand.SetAction(static parseResult =>
        {
            var inputAssembly = parseResult.GetValue(s_inputOption);
            var references = parseResult.GetValue(s_referencesOption);
            var outputFile = parseResult.GetValue(s_outputOption);
            var version = parseResult.GetValue(s_versionOption);
            var packageName = parseResult.GetValue(s_packageNameOption);
            var sourceRepo = parseResult.GetValue(s_sourceRepoOption);
            var sourceCommit = parseResult.GetValue(s_sourceCommitOption);
            var sourceRoot = parseResult.GetValue(s_sourceRootOption);
            var sourceFileManifest = parseResult.GetValue(s_sourceFileManifestOption);
            var targetFramework = parseResult.GetValue(s_targetFrameworkOption);

            PackageJsonGenerator.GeneratePackageJson(inputAssembly, references, outputFile, version, packageName, sourceRepo, sourceCommit, sourceRoot, sourceFileManifest, targetFramework);
            return 0;
        });

        return formatCommand;
    }
}