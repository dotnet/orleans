// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using PackageJsonGenerator.Helpers;

namespace PackageJsonGenerator;

public static class PackageJsonGenerator
{
    private const string OrleansRepositoryUrl = "https://github.com/dotnet/orleans";

    public static void GeneratePackageJson(string? inputAssembly, string[]? references, string? outputFile, string? versionOverride = null, string? packageNameOverride = null, string? sourceRepoOverride = null, string? sourceCommitOverride = null, string? sourceRootOverride = null, string? sourceFileManifestOverride = null, string? targetFrameworkOverride = null, ConcurrentDictionary<string, PortableExecutableReference>? referenceCache = null)
    {
        if (string.IsNullOrEmpty(inputAssembly))
        {
            throw new ArgumentException("Input assembly path is required.", nameof(inputAssembly));
        }

        if (references is null || references.Length is 0)
        {
            throw new ArgumentException("At least one reference assembly is required.", nameof(references));
        }

        if (string.IsNullOrEmpty(outputFile))
        {
            throw new ArgumentException("Output file path is required.", nameof(outputFile));
        }

        var inputReference = CreateMetadataReference(inputAssembly);
        var resolvedRefs = referenceCache is not null
            ? references.Select(r => referenceCache.GetOrAdd(r, CreateMetadataReference))
            : references.Select(CreateMetadataReference);
        var compilation = CSharpCompilation.Create(
            "PackageJsonGen",
            references: resolvedRefs.Concat([inputReference]));

        var assemblySymbol = (IAssemblySymbol)compilation.GetAssemblyOrModuleSymbol(inputReference)!;

        // Collect all public types from the assembly
        var types = new List<INamedTypeSymbol>();
        CollectTypes(assemblySymbol.GlobalNamespace, types, assemblySymbol);

        if (types.Count == 0)
        {
            Console.WriteLine($"No public types found in assembly: {assemblySymbol.Name}");
        }

        var assemblyName = !string.IsNullOrEmpty(packageNameOverride)
            ? packageNameOverride
            : assemblySymbol.Name;
        var assemblyVersion = !string.IsNullOrEmpty(versionOverride)
            ? versionOverride
            : assemblySymbol.Identity.Version.ToString();
        var targetFramework = !string.IsNullOrEmpty(targetFrameworkOverride)
            ? targetFrameworkOverride
            : "net10.0";

        // Resolve source link info from assembly metadata or CLI overrides
        var sourceRepo = sourceRepoOverride;
        var sourceCommit = sourceCommitOverride;

        if (string.IsNullOrEmpty(sourceRepo) || string.IsNullOrEmpty(sourceCommit))
        {
            foreach (var attr in assemblySymbol.GetAttributes())
            {
                if (attr.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyMetadataAttribute"
                    && attr.ConstructorArguments.Length == 2)
                {
                    var key = attr.ConstructorArguments[0].Value as string;
                    var value = attr.ConstructorArguments[1].Value as string;

                    if (string.IsNullOrEmpty(sourceRepo) && key == "RepositoryUrl")
                        sourceRepo = value;
                    if (string.IsNullOrEmpty(sourceCommit) && key == "RepositoryCommit")
                        sourceCommit = value;
                }

                // Fallback: extract commit from InformationalVersion (e.g. "1.2.3+abc123def")
                if (string.IsNullOrEmpty(sourceCommit)
                    && attr.AttributeClass?.ToDisplayString() == "System.Reflection.AssemblyInformationalVersionAttribute"
                    && attr.ConstructorArguments.Length == 1)
                {
                    var infoVersion = attr.ConstructorArguments[0].Value as string;
                    if (infoVersion is not null)
                    {
                        var plusIdx = infoVersion.IndexOf('+');
                        if (plusIdx >= 0 && plusIdx + 1 < infoVersion.Length)
                        {
                            sourceCommit = infoVersion[(plusIdx + 1)..];
                        }
                    }
                }
            }
        }

        sourceRepo = NormalizeSourceRepository(sourceRepo);
        var trackedSourceFiles = LoadSourceFileManifest(sourceFileManifestOverride);

        // Build canonical models
        var modelBuilder = new CanonicalModelBuilder(compilation);
        var typeModels = modelBuilder.BuildTypes(types.ToImmutableArray());

        // Enrich with PDB source info (file paths + line ranges)
        using (var pdbReader = new PdbSourceReader(inputAssembly))
        {
            if (pdbReader.HasPdb)
            {
                foreach (var typeModel in typeModels)
                {
                    var typeSource = pdbReader.GetTypeSource(typeModel.FullName);
                    if (typeSource is not null &&
                        IsSourceFileAvailable(typeSource.File, sourceRootOverride, trackedSourceFiles))
                    {
                        typeModel.SourceFile = typeSource.File;
                        // Use only the start line as a single-line anchor. The PDB
                        // aggregate range spans method implementations, not the type
                        // declaration. A start-line anchor locates the class (especially
                        // in multi-type files) without suggesting a misleading range.
                        typeModel.SourceLines = $"{typeSource.StartLine}-{typeSource.StartLine}";
                    }

                    foreach (var member in typeModel.Members)
                    {
                        var paramNames = member.Parameters?.Select(p => p.Name).ToList();
                        var memberSource = pdbReader.GetMemberSource(typeModel.FullName, member.Name, paramNames);
                        if (memberSource is not null &&
                            IsSourceFileAvailable(memberSource.File, sourceRootOverride, trackedSourceFiles))
                        {
                            member.SourceFile = memberSource.File;
                            if (memberSource.StartLine > 0)
                            {
                                member.SourceLines = memberSource.ToLineRange();
                            }
                        }
                    }
                }
            }

            // Fallback: infer source file paths for types that had no PDB sequence points
            // (interfaces without default methods, enums, delegates, const-only classes)
            foreach (var typeModel in typeModels)
            {
                if (typeModel.SourceFile is null)
                {
                    var inferredSourceFile = pdbReader.InferTypeSourceFile(typeModel.FullName, typeModel.Namespace);
                    if (inferredSourceFile is not null &&
                        IsSourceFileAvailable(inferredSourceFile, sourceRootOverride, trackedSourceFiles))
                    {
                        typeModel.SourceFile = inferredSourceFile;
                    }
                }
            }
        }

        // PDB-based type line info often points at the first method body rather than
        // the type declaration. Fetch the source file and resolve declaration lines
        // for all types when source information is available.
        AdjustSourceLinesForTypeDeclarations(typeModels, sourceRootOverride);

        // Emit JSON schema
        var schemaJson = SchemaEmitter.EmitAssemblySchema(
            assemblyName,
            assemblyVersion,
            targetFramework,
            typeModels,
            sourceRepo,
            sourceCommit);

        // Ensure output directory exists
        var outputDir = Path.GetDirectoryName(outputFile);
        if (!string.IsNullOrEmpty(outputDir) && !Directory.Exists(outputDir))
        {
            Directory.CreateDirectory(outputDir);
        }

        var wroteFile = StableFileWriter.WriteIfChanged(outputFile, schemaJson);
        Console.WriteLine($"{(wroteFile ? "Generated" : "Unchanged")}: {outputFile}");
    }

    private static HashSet<string>? LoadSourceFileManifest(string? sourceFileManifest)
    {
        if (string.IsNullOrEmpty(sourceFileManifest))
        {
            return null;
        }

        if (!File.Exists(sourceFileManifest))
        {
            throw new FileNotFoundException("Source file manifest not found.", sourceFileManifest);
        }

        return File.ReadLines(sourceFileManifest)
            .Where(static line => !string.IsNullOrWhiteSpace(line))
            .Select(static line => line.Replace('\\', '/'))
            .ToHashSet(StringComparer.Ordinal);
    }

    private static bool IsSourceFileAvailable(
        string sourceFile,
        string? sourceRoot,
        HashSet<string>? trackedSourceFiles)
    {
        sourceFile = sourceFile.Replace('\\', '/');
        if (sourceFile.Split('/').Any(static segment =>
            segment.Equals("bin", StringComparison.OrdinalIgnoreCase) ||
            segment.Equals("obj", StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        if (trackedSourceFiles is not null && !trackedSourceFiles.Contains(sourceFile))
        {
            return false;
        }

        if (string.IsNullOrEmpty(sourceRoot))
        {
            return true;
        }

        sourceRoot = Path.GetFullPath(sourceRoot);
        var candidatePath = Path.GetFullPath(Path.Combine(sourceRoot, sourceFile));
        var sourceRootPrefix = sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return candidatePath.StartsWith(sourceRootPrefix, pathComparison) && File.Exists(candidatePath);
    }

    internal static PortableExecutableReference CreateMetadataReference(string path)
    {
        var docPath = Path.ChangeExtension(path, "xml");
        var documentationProvider = File.Exists(docPath)
            ? XmlDocumentationProvider.CreateFromFile(docPath)
            : null;

        return MetadataReference.CreateFromFile(path, documentation: documentationProvider);
    }

    private static void CollectTypes(
        INamespaceSymbol ns,
        List<INamedTypeSymbol> types,
        IAssemblySymbol targetAssembly)
    {
        foreach (var type in ns.GetTypeMembers())
        {
            CollectTypeAndNested(type, types);
        }

        foreach (var childNs in ns.GetNamespaceMembers())
        {
            if (SymbolEqualityComparer.Default.Equals(childNs.ContainingAssembly, targetAssembly))
            {
                CollectTypes(childNs, types, targetAssembly);
            }
        }
    }

    private static void CollectTypeAndNested(INamedTypeSymbol type, List<INamedTypeSymbol> types)
    {
        if (type.DeclaredAccessibility == Accessibility.Public && !IsCompilerGenerated(type)
            && !string.IsNullOrEmpty(type.Name))
        {
            types.Add(type);

            // Also collect public nested types
            foreach (var nestedType in type.GetTypeMembers())
            {
                CollectTypeAndNested(nestedType, types);
            }
        }
    }

    private static bool IsCompilerGenerated(INamedTypeSymbol type)
    {
        return type.Name.StartsWith("<", StringComparison.Ordinal) ||
               type.GetAttributes().Any(a =>
                   a.AttributeClass?.Name == "CompilerGeneratedAttribute");
    }

    /// <summary>
    /// For source files that contain multiple types, fetches the actual source text
    /// from the repository and finds the exact type declaration line. PDB sequence
    /// points only cover method bodies, so the aggregated StartLine can overshoot
    /// the real <c>class</c>/<c>interface</c>/<c>struct</c>/etc. keyword by several lines.
    /// </summary>
    private static void AdjustSourceLinesForTypeDeclarations(
        List<CanonicalType> typeModels,
        string? sourceRoot)
    {
        if (string.IsNullOrEmpty(sourceRoot))
            return;

        sourceRoot = Path.GetFullPath(sourceRoot);
        var sourceFiles = typeModels
            .Where(t => t.SourceFile is not null)
            .GroupBy(t => t.SourceFile!)
            .ToList();

        if (sourceFiles.Count == 0) return;

        // Cache source files so each file is read at most once.
        var sourceCache = new Dictionary<string, string[]?>();

        foreach (var group in sourceFiles)
        {
            var lines = GetSourceLines(sourceCache, sourceRoot, group.Key);
            if (lines is null)
            {
                // Source unavailable — keep any existing PDB-based values rather than
                // inventing a new line anchor.
                foreach (var typeModel in group)
                {
                    if (typeModel.SourceLines is null)
                    {
                        typeModel.SourceLines = null;
                    }
                }
                continue;
            }

            foreach (var typeModel in group)
            {
                var simpleName = PdbSourceReader.NormalizeGenericName(typeModel.Name);

                // For nested types like "Outer.Inner", use just the inner name
                var dotIdx = simpleName.LastIndexOf('.');
                if (dotIdx >= 0) simpleName = simpleName[(dotIdx + 1)..];

                var pdbHintLine = ParseStartLine(typeModel.SourceLines);
                var declarationLine = FindTypeDeclarationLine(lines, simpleName, pdbHintLine);
                if (declarationLine > 0)
                {
                    typeModel.SourceLines = $"{declarationLine}-{declarationLine}";
                }
                else if (typeModel.SourceLines is not null)
                {
                    // Regex found no match — drop the PDB-based line so the link
                    // points to the source file itself rather than a wrong line.
                    typeModel.SourceLines = null;
                }
            }
        }
    }

    /// <summary>
    /// Returns the source lines for the given file, reading from the repository on first
    /// access and caching for subsequent lookups.
    /// </summary>
    private static string[]? GetSourceLines(
        Dictionary<string, string[]?> cache,
        string sourceRoot,
        string filePath)
    {
        if (cache.TryGetValue(filePath, out var cached))
            return cached;

        string[]? lines = null;
        var candidatePath = Path.GetFullPath(Path.Combine(sourceRoot, filePath));
        var sourceRootPrefix = sourceRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (candidatePath.StartsWith(sourceRootPrefix, pathComparison) &&
            File.Exists(candidatePath))
        {
            lines = File.ReadAllLines(candidatePath);
        }

        cache[filePath] = lines;
        return lines;
    }

    internal static string? BuildRawGitHubUrl(string sourceRepo, string sourceCommit, string filePath)
    {
        sourceRepo = NormalizeSourceRepository(sourceRepo) ?? sourceRepo;

        if (!Uri.TryCreate(sourceRepo, UriKind.Absolute, out var repoUri))
            return null;

        if (!repoUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
            return null;

        var repoPath = repoUri.AbsolutePath.TrimEnd('/');
        if (repoPath.EndsWith(".git", StringComparison.OrdinalIgnoreCase))
            repoPath = repoPath[..^4];

        return $"https://raw.githubusercontent.com{repoPath}/{sourceCommit}/{filePath}";
    }

    internal static string? NormalizeSourceRepository(string? sourceRepo)
    {
        if (string.IsNullOrWhiteSpace(sourceRepo))
        {
            return sourceRepo;
        }

        var trimmed = sourceRepo.Trim();

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var repoUri) &&
            repoUri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase))
        {
            var segments = repoUri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 2 &&
                segments[0].Equals("dotnet", StringComparison.OrdinalIgnoreCase) &&
                (segments[1].Equals("orleans", StringComparison.OrdinalIgnoreCase) ||
                 segments[1].Equals("orleans.git", StringComparison.OrdinalIgnoreCase)))
            {
                return OrleansRepositoryUrl;
            }
        }

        return trimmed;
    }

    /// <summary>
    /// Scans source lines for a type declaration matching <paramref name="typeName"/>.
    /// When multiple matches exist (e.g. a nested type reusing a common name),
    /// the match closest to <paramref name="pdbHintLine"/> wins.
    /// </summary>
    internal static int FindTypeDeclarationLine(string[] lines, string typeName, int pdbHintLine)
    {
        var escapedName = Regex.Escape(typeName);
        var pattern = new Regex(
            $@"\b(?:class|interface|struct|enum|record|extension)\s+{escapedName}\b",
            RegexOptions.Compiled);

        int bestLine = -1;
        int bestDistance = int.MaxValue;

        for (int i = 0; i < lines.Length; i++)
        {
            var trimmed = lines[i].TrimStart();

            // Skip comment lines
            if (trimmed.StartsWith("//", StringComparison.Ordinal)
                || trimmed.StartsWith("/*", StringComparison.Ordinal)
                || trimmed.StartsWith("*", StringComparison.Ordinal))
                continue;

            if (pattern.IsMatch(lines[i]))
            {
                int lineNum = i + 1; // 1-indexed
                int distance = pdbHintLine > 0 ? Math.Abs(lineNum - pdbHintLine) : 0;
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    bestLine = lineNum;
                }
            }
        }

        return bestLine;
    }

    internal static int ParseStartLine(string? sourceLines)
    {
        if (sourceLines is null) return 0;
        var dash = sourceLines.IndexOf('-');
        return dash > 0 && int.TryParse(sourceLines[..dash], out var line) ? line : 0;
    }
}
