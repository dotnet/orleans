using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

const string ProjectMarker = "ORLEANS_CONTRACT_PROJECT=";
string[] diagnosticIds =
[
    "ORLEANS0016",
    "ORLEANS0017",
    "ORLEANS0018",
    "ORLEANS0019",
    "ORLEANS0020",
    "ORLEANS0022",
    "ORLEANS0023",
    "ORLEANS0024",
];

var dryRun = args.Contains("--dry-run", StringComparer.Ordinal);
var paths = args.Where(arg => !string.Equals(arg, "--dry-run", StringComparison.Ordinal)).ToArray();
var pathComparer = OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal;
var contractDeclarationPattern = new Regex(
    @"(?:\[\s*(?:global::)?(?:Orleans\.)?(?:GrainType|GrainInterfaceType)\b)|(?:\b(?:interface|class|record)\s+[\w@][^{;]*:\s*[^{;]*(?:\bIGrain\w*\b|\bIAddressable\b|\bGrain(?:<|\b)))",
    RegexOptions.Compiled | RegexOptions.CultureInvariant);
if (paths.Length != 1)
{
    Console.Error.WriteLine("Usage: orleans-contracts [--dry-run] <project-or-solution>");
    return 1;
}

var inputPath = Path.GetFullPath(paths[0]);
if (!File.Exists(inputPath))
{
    Console.Error.WriteLine($"Project or solution not found: {inputPath}");
    return 1;
}

if (string.Equals(Path.GetExtension(inputPath), ".csproj", StringComparison.OrdinalIgnoreCase))
{
    if (dryRun)
    {
        Console.WriteLine(inputPath);
        return 0;
    }

    return await RunDotNetFormatAsync(inputPath, diagnosticIds, Path.GetDirectoryName(inputPath)!);
}

var extension = Path.GetExtension(inputPath);
if (!string.Equals(extension, ".sln", StringComparison.OrdinalIgnoreCase)
    && !string.Equals(extension, ".slnx", StringComparison.OrdinalIgnoreCase))
{
    Console.Error.WriteLine("Expected a .csproj, .sln, or .slnx path.");
    return 1;
}

var temporaryDirectory = Path.Combine(Path.GetTempPath(), "OrleansContracts", Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(temporaryDirectory);
try
{
    var targetsPath = Path.Combine(temporaryDirectory, "CollectOrleansContractProjects.targets");
    await File.WriteAllTextAsync(
        targetsPath,
        """
        <Project>
          <Target Name="_CollectOrleansContractProjects">
            <Message Condition="'$(EnableOrleansContractsAnalyzer)' == 'true'"
                     Importance="high"
                     Text="ORLEANS_CONTRACT_PROJECT=$(MSBuildProjectFullPath)::$(OrleansContractsPath)" />
          </Target>
        </Project>
        """);

    var discovery = await RunProcessAsync(
        "dotnet",
        [
            "msbuild",
            inputPath,
            "-t:_CollectOrleansContractProjects",
            "-m",
            "-nologo",
            "-v:minimal",
            $"-p:CustomAfterMicrosoftCommonTargets={targetsPath}",
            $"-p:CustomAfterMicrosoftCommonCrossTargetingTargets={targetsPath}",
        ],
        Path.GetDirectoryName(inputPath)!,
        captureOutput: true);
    if (discovery.ExitCode != 0)
    {
        Console.Error.Write(discovery.StandardError);
        Console.Error.Write(discovery.StandardOutput);
        return discovery.ExitCode;
    }

    var projects = discovery.StandardOutput
        .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
        .Select(line => line.Trim())
        .Where(line => line.Contains(ProjectMarker, StringComparison.Ordinal))
        .Select(line => line[(line.IndexOf(ProjectMarker, StringComparison.Ordinal) + ProjectMarker.Length)..].Trim())
        .Select(value =>
        {
            var separatorIndex = value.IndexOf("::", StringComparison.Ordinal);
            return separatorIndex < 0
                ? new ContractProject(value, string.Empty)
                : new ContractProject(value[..separatorIndex], value[(separatorIndex + 2)..]);
        })
        .Where(project =>
            File.Exists(project.ProjectPath)
            && string.Equals(Path.GetExtension(project.ProjectPath), ".csproj", StringComparison.OrdinalIgnoreCase)
            && (File.Exists(project.ContractsPath) || ContainsContractDeclarations(project.ProjectPath)))
        .DistinctBy(project => project.ProjectPath, pathComparer)
        .OrderBy(project => project.ProjectPath, pathComparer)
        .ToArray();
    if (projects.Length == 0)
    {
        Console.WriteLine("No projects have EnableOrleansContractsAnalyzer set to true.");
        return 0;
    }

    if (dryRun)
    {
        foreach (var project in projects)
        {
            Console.WriteLine(project.ProjectPath);
        }

        return 0;
    }

    var solutionDirectory = Path.GetDirectoryName(inputPath)!;
    var filteredSolutionPath = Path.Combine(temporaryDirectory, "OrleansContracts.slnx");
    try
    {
        var content = new StringBuilder();
        content.AppendLine("<Solution>");
        foreach (var project in projects)
        {
            var relativePath = Path.GetRelativePath(temporaryDirectory, project.ProjectPath).Replace('\\', '/');
            content.Append("  <Project Path=\"")
                .Append(System.Security.SecurityElement.Escape(relativePath))
                .AppendLine("\" />");
        }

        content.AppendLine("</Solution>");
        await File.WriteAllTextAsync(filteredSolutionPath, content.ToString());

        Console.WriteLine($"Regenerating Orleans contracts in {projects.Length} project(s).");
        return await RunDotNetFormatAsync(filteredSolutionPath, diagnosticIds, solutionDirectory);
    }
    finally
    {
        File.Delete(filteredSolutionPath);
    }
}
finally
{
    Directory.Delete(temporaryDirectory, recursive: true);
}

bool ContainsContractDeclarations(string projectPath)
{
    var projectDirectory = Path.GetDirectoryName(projectPath)!;
    foreach (var sourcePath in Directory.EnumerateFiles(projectDirectory, "*.cs", SearchOption.AllDirectories))
    {
        var relativePath = Path.GetRelativePath(projectDirectory, sourcePath);
        if (relativePath.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(segment => segment is "bin" or "obj"))
        {
            continue;
        }

        if (contractDeclarationPattern.IsMatch(File.ReadAllText(sourcePath)))
        {
            return true;
        }
    }

    return false;
}

static async Task<int> RunDotNetFormatAsync(
    string path,
    string[] diagnosticIds,
    string workingDirectory)
{
    var arguments = new List<string>
    {
        "format",
        path,
        "analyzers",
        "--severity",
        "info",
        "--diagnostics",
    };
    arguments.AddRange(diagnosticIds);
    var result = await RunProcessAsync(
        "dotnet",
        arguments,
        workingDirectory,
        captureOutput: false);
    return result.ExitCode;
}

static async Task<ProcessResult> RunProcessAsync(
    string fileName,
    IReadOnlyList<string> arguments,
    string workingDirectory,
    bool captureOutput)
{
    var startInfo = new ProcessStartInfo(fileName)
    {
        WorkingDirectory = workingDirectory,
        UseShellExecute = false,
        RedirectStandardOutput = captureOutput,
        RedirectStandardError = captureOutput,
    };
    foreach (var argument in arguments)
    {
        startInfo.ArgumentList.Add(argument);
    }

    using var process = Process.Start(startInfo);
    if (process is null)
    {
        throw new InvalidOperationException($"Could not start {fileName}.");
    }

    var standardOutput = captureOutput ? process.StandardOutput.ReadToEndAsync() : Task.FromResult(string.Empty);
    var standardError = captureOutput ? process.StandardError.ReadToEndAsync() : Task.FromResult(string.Empty);
    await process.WaitForExitAsync();
    return new ProcessResult(process.ExitCode, await standardOutput, await standardError);
}

readonly record struct ProcessResult(int ExitCode, string StandardOutput, string StandardError);

readonly record struct ContractProject(string ProjectPath, string ContractsPath);
