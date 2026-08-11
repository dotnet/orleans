// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.CommandLine;
using System.Text.Json;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

namespace PackageJsonGenerator;

internal static class CSharpFenceSyntaxCommand
{
    private static readonly Option<string> s_inputOption = new("--input")
    {
        Required = true,
        Description = "Path to a JSON array containing C# fence sources.",
    };

    private static readonly Option<string> s_outputOption = new("--output")
    {
        Required = true,
        Description = "Path for the JSON array of fences which are invalid in every supported context.",
    };

    private static readonly CSharpParseOptions s_parseOptions = new(
        languageVersion: LanguageVersion.Preview,
        kind: SourceCodeKind.Regular);

    public static Command GetCommand()
    {
        var command = new Command(
            "validate-csharp-fences",
            "Validates C# fragments in common compilation contexts.")
        {
            s_inputOption,
            s_outputOption,
        };

        command.SetAction(static parseResult =>
        {
            var input = parseResult.GetValue(s_inputOption)!;
            var output = parseResult.GetValue(s_outputOption)!;
            return ValidateFile(input, output);
        });

        return command;
    }

    internal static bool IsValidInAnyContext(string source) =>
        CandidateSources(source).Any(IsValidSyntax);

    private static IEnumerable<string> CandidateSources(string source)
    {
        yield return source;
        yield return $$"""
            namespace Orleans.Docs.FenceValidation;
            public class Context
            {
            {{source}}
            }
            """;
        yield return $$"""
            namespace Orleans.Docs.FenceValidation;
            public interface Context
            {
            {{source}}
            }
            """;
        yield return $$"""
            namespace Orleans.Docs.FenceValidation;
            public class Context
            {
                public void Method()
                {
            {{source}}
                }
            }
            """;
        yield return $$"""
            namespace Orleans.Docs.FenceValidation;
            public class Context
            {
                public object? Method() => {{source}};
            }
            """;
        yield return $$"""
            namespace Orleans.Docs.FenceValidation;
            {{source}}
            public class Target;
            """;
    }

    private static bool IsValidSyntax(string source) =>
        !CSharpSyntaxTree.ParseText(source, s_parseOptions)
            .GetDiagnostics()
            .Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

    private static int ValidateFile(string inputPath, string outputPath)
    {
        if (!File.Exists(inputPath))
        {
            Console.Error.WriteLine($"C# fence input not found: {inputPath}");
            return 1;
        }

        var options = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        var fences = JsonSerializer.Deserialize<List<CSharpFence>>(File.ReadAllText(inputPath), options);
        if (fences is null)
        {
            Console.Error.WriteLine("C# fence input is not a JSON array.");
            return 1;
        }

        var invalid = fences
            .Where(fence => !IsValidInAnyContext(fence.Source))
            .ToArray();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outputPath))!);
        File.WriteAllText(outputPath, JsonSerializer.Serialize(invalid, options));
        Console.WriteLine(
            $"Validated {fences.Count} inline C# fences; {invalid.Length} require explicit exclusions.");
        return 0;
    }

    private sealed class CSharpFence
    {
        public string File { get; set; } = "";

        public int Line { get; set; }

        public string Hash { get; set; } = "";

        public string Source { get; set; } = "";
    }
}
