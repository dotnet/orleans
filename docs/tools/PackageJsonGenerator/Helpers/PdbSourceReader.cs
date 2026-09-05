// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace PackageJsonGenerator.Helpers;

/// <summary>
/// Reads source file paths and line ranges from an embedded portable PDB.
/// Builds a lookup from metadata token → (file, startLine, endLine) that
/// <see cref="CanonicalModelBuilder"/> uses to populate SourceFile/SourceLines.
/// </summary>
internal sealed class PdbSourceReader : IDisposable
{
    private readonly PEReader _peReader;
    private readonly MetadataReaderProvider? _pdbProvider;
    private readonly MetadataReader? _pdbReader;
    private readonly MetadataReader _peMetadata;

    /// <summary>
    /// Per-method source info keyed by MethodDef row number.
    /// </summary>
    private readonly Dictionary<int, MethodSourceInfo> _methodSources = [];

    /// <summary>
    /// Per-type aggregated source info keyed by TypeDef row number.
    /// </summary>
    private readonly Dictionary<int, MethodSourceInfo> _typeSources = [];

    /// <summary>
    /// Maps normalized full type names (generics stripped) to their TypeDef handles.
    /// Enables O(1) lookup that correctly handles nested types and generic arity.
    /// </summary>
    private readonly Dictionary<string, TypeDefinitionHandle> _normalizedNameToHandle = [];

    /// <summary>
    /// Maps namespaces to their source directory paths, derived from types that have PDB source info.
    /// Used as a fallback to infer source file paths for types without method bodies.
    /// </summary>
    private readonly Dictionary<string, string> _namespaceToDirs = [];

    /// <summary>
    /// The common base directory for source files in this assembly (e.g. "src/Orleans.Core/").
    /// </summary>
    private string? _baseDir;

    public bool HasPdb => _pdbReader is not null;

    public PdbSourceReader(string assemblyPath)
    {
        _peReader = new PEReader(File.OpenRead(assemblyPath));
        _peMetadata = _peReader.GetMetadataReader();

        var debugEntries = _peReader.ReadDebugDirectory();
        var embeddedEntry = Array.Find(
            debugEntries.ToArray(),
            e => e.Type == DebugDirectoryEntryType.EmbeddedPortablePdb);

        if (embeddedEntry.Type == DebugDirectoryEntryType.EmbeddedPortablePdb)
        {
            _pdbProvider = _peReader.ReadEmbeddedPortablePdbDebugDirectoryData(embeddedEntry);
            _pdbReader = _pdbProvider.GetMetadataReader();
            BuildMethodIndex();
            BuildTypeIndex();
        }
        else
        {
            // Try standalone PDB
            var pdbPath = Path.ChangeExtension(assemblyPath, ".pdb");
            if (File.Exists(pdbPath))
            {
                var pdbStream = File.OpenRead(pdbPath);
                _pdbProvider = MetadataReaderProvider.FromPortablePdbStream(pdbStream);
                _pdbReader = _pdbProvider.GetMetadataReader();
                BuildMethodIndex();
                BuildTypeIndex();
            }
        }

        BuildNameIndex();
        BuildNamespaceDirMapping();
    }

    private void BuildMethodIndex()
    {
        if (_pdbReader is null) return;

        foreach (var handle in _pdbReader.MethodDebugInformation)
        {
            var mdi = _pdbReader.GetMethodDebugInformation(handle);
            if (mdi.Document.IsNil) continue;

            var doc = _pdbReader.GetDocument(mdi.Document);
            var docName = _pdbReader.GetString(doc.Name);

            int minLine = int.MaxValue, maxLine = 0;
            foreach (var sp in mdi.GetSequencePoints())
            {
                if (sp.IsHidden) continue;
                if (sp.StartLine < minLine) minLine = sp.StartLine;
                if (sp.EndLine > maxLine) maxLine = sp.EndLine;
            }

            if (minLine < int.MaxValue)
            {
                var rowId = MetadataTokens.GetRowNumber(handle);
                _methodSources[rowId] = new MethodSourceInfo(CleanPath(docName), minLine, maxLine);
            }
        }
    }

    private void BuildTypeIndex()
    {
        foreach (var typeDefHandle in _peMetadata.TypeDefinitions)
        {
            var typeDef = _peMetadata.GetTypeDefinition(typeDefHandle);
            string? file = null;
            int typeMinLine = int.MaxValue, typeMaxLine = 0;

            AggregateMethodSources(typeDef, ref file, ref typeMinLine, ref typeMaxLine);

            if (file is not null)
            {
                var typeRowId = MetadataTokens.GetRowNumber(typeDefHandle);
                _typeSources[typeRowId] = new MethodSourceInfo(file, typeMinLine, typeMaxLine);
            }
        }
    }

    /// <summary>
    /// Builds a dictionary mapping normalized full type names to their TypeDef handles.
    /// This enables O(1) lookup that correctly handles nested types and generic arity.
    /// </summary>
    private void BuildNameIndex()
    {
        foreach (var handle in _peMetadata.TypeDefinitions)
        {
            var fullName = GetFullMetadataName(handle);
            var normalized = NormalizeGenericName(fullName);
            _normalizedNameToHandle.TryAdd(normalized, handle);
        }
    }

    /// <summary>
    /// Builds namespace → directory mappings from types that have known source file paths.
    /// Also determines the common base directory for the assembly's source files.
    /// </summary>
    private void BuildNamespaceDirMapping()
    {
        foreach (var (rowId, source) in _typeSources)
        {
            var handle = MetadataTokens.TypeDefinitionHandle(rowId);
            var typeDef = _peMetadata.GetTypeDefinition(handle);
            var ns = _peMetadata.GetString(typeDef.Namespace);

            // For nested types, walk up to the containing type's namespace
            if (string.IsNullOrEmpty(ns))
            {
                var parent = typeDef.GetDeclaringType();
                while (!parent.IsNil)
                {
                    var parentDef = _peMetadata.GetTypeDefinition(parent);
                    ns = _peMetadata.GetString(parentDef.Namespace);
                    if (!string.IsNullOrEmpty(ns)) break;
                    parent = parentDef.GetDeclaringType();
                }
            }

            if (string.IsNullOrEmpty(ns) || string.IsNullOrEmpty(source.File)) continue;

            var lastSlash = source.File.LastIndexOf('/');
            if (lastSlash > 0)
            {
                var dir = source.File[..(lastSlash + 1)];
                _namespaceToDirs.TryAdd(ns, dir);
            }
        }

        // Determine the common base directory (e.g. "src/Orleans.Core/")
        var baseDirs = _typeSources.Values
            .Select(s => s.File)
            .Where(f => f.StartsWith("src/", StringComparison.Ordinal))
            .Select(f =>
            {
                var secondSlash = f.IndexOf('/', 4);
                return secondSlash > 0 ? f[..(secondSlash + 1)] : null;
            })
            .Where(d => d is not null)
            .Distinct()
            .ToList();

        if (baseDirs.Count == 1)
        {
            _baseDir = baseDirs[0];
        }
    }

    /// <summary>
    /// Builds the full metadata name for a type, correctly handling nested types
    /// by walking the declaring type chain.
    /// </summary>
    private string GetFullMetadataName(TypeDefinitionHandle handle)
    {
        var typeDef = _peMetadata.GetTypeDefinition(handle);
        var name = _peMetadata.GetString(typeDef.Name);

        if (!typeDef.GetDeclaringType().IsNil)
        {
            var parentName = GetFullMetadataName(typeDef.GetDeclaringType());
            return $"{parentName}.{name}";
        }

        var ns = _peMetadata.GetString(typeDef.Namespace);
        return string.IsNullOrEmpty(ns) ? name : $"{ns}.{name}";
    }

    /// <summary>
    /// Normalizes Roslyn generic type syntax to PE metadata arity notation so
    /// names with different arities remain distinct.
    /// <c>InteractionResult`1</c> and <c>InteractionResult&lt;T&gt;</c> both normalize
    /// to <c>InteractionResult`1</c>.
    /// </summary>
    internal static string NormalizeGenericName(string name)
    {
        if (!name.Contains('<'))
            return name;

        var sb = new StringBuilder(name.Length);
        int i = 0;
        while (i < name.Length)
        {
            if (name[i] == '<')
            {
                int depth = 1;
                int arity = 1;
                i++;
                while (i < name.Length && depth > 0)
                {
                    if (name[i] == '<') depth++;
                    else if (name[i] == '>') depth--;
                    else if (name[i] == ',' && depth == 1) arity++;
                    i++;
                }
                sb.Append('`');
                sb.Append(arity);
            }
            else
            {
                sb.Append(name[i]);
                i++;
            }
        }
        return sb.ToString();
    }

    private static string StripGenericArity(string name)
    {
        var sb = new StringBuilder(name.Length);
        int i = 0;
        while (i < name.Length)
        {
            if (name[i] == '`')
            {
                i++;
                while (i < name.Length && char.IsDigit(name[i])) i++;
            }
            else if (name[i] == '<')
            {
                int depth = 1;
                i++;
                while (i < name.Length && depth > 0)
                {
                    if (name[i] == '<') depth++;
                    else if (name[i] == '>') depth--;
                    i++;
                }
            }
            else
            {
                sb.Append(name[i]);
                i++;
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// Aggregates source info from all methods in the given type and its nested types.
    /// Nested types include compiler-generated state machines for async/iterator methods
    /// whose sequence points map back to the enclosing type's source file.
    /// </summary>
    private void AggregateMethodSources(TypeDefinition typeDef, ref string? file, ref int minLine, ref int maxLine)
    {
        foreach (var methodHandle in typeDef.GetMethods())
        {
            var rowId = MetadataTokens.GetRowNumber(methodHandle);
            if (_methodSources.TryGetValue(rowId, out var info))
            {
                file ??= info.File;
                if (info.StartLine < minLine) minLine = info.StartLine;
                if (info.EndLine > maxLine) maxLine = info.EndLine;
            }
        }

        foreach (var nestedHandle in typeDef.GetNestedTypes())
        {
            var nestedType = _peMetadata.GetTypeDefinition(nestedHandle);
            AggregateMethodSources(nestedType, ref file, ref minLine, ref maxLine);
        }
    }

    /// <summary>
    /// Looks up source info for a type by its full name (as produced by Roslyn's ToDisplayString).
    /// Handles nested types and generic type names via normalized name matching.
    /// Returns null if no PDB info available.
    /// </summary>
    public MethodSourceInfo? GetTypeSource(string fullMetadataName)
    {
        if (_pdbReader is null) return null;

        var normalized = NormalizeGenericName(fullMetadataName);
        if (_normalizedNameToHandle.TryGetValue(normalized, out var handle))
        {
            var rowId = MetadataTokens.GetRowNumber(handle);
            return _typeSources.GetValueOrDefault(rowId);
        }

        return null;
    }

    /// <summary>
    /// For types that have no PDB sequence points (interfaces, enums, delegates, const-only classes),
    /// infers a probable source file path from the namespace→directory mapping and type name.
    /// Returns just the file path (no line numbers), or null if inference isn't possible.
    /// </summary>
    public string? InferTypeSourceFile(string fullName, string namespaceName)
    {
        if (string.IsNullOrEmpty(namespaceName)) return null;

        // Strip namespace prefix to get the type path
        var typePath = fullName.Length > namespaceName.Length + 1
            ? fullName[(namespaceName.Length + 1)..]
            : fullName;

        // Normalize away generic parameters
        typePath = StripGenericArity(typePath);

        // For nested types, use the outermost type's name as the file name
        var topLevelName = typePath.Contains('.')
            ? typePath[..typePath.IndexOf('.')]
            : typePath;
        var fileName = $"{topLevelName}.cs";

        // Try direct namespace → directory lookup first
        if (_namespaceToDirs.TryGetValue(namespaceName, out var dir))
        {
            return $"{dir}{fileName}";
        }

        // Fallback: construct the path from the base directory + relative namespace
        if (_baseDir is not null)
        {
            var assemblyName = _baseDir.TrimEnd('/');
            var lastSlash = assemblyName.LastIndexOf('/');
            if (lastSlash >= 0)
            {
                assemblyName = assemblyName[(lastSlash + 1)..];
            }

            var relativeNs = namespaceName.StartsWith(assemblyName + ".", StringComparison.Ordinal)
                ? namespaceName[(assemblyName.Length + 1)..]
                : namespaceName == assemblyName
                    ? ""
                    : null;

            if (relativeNs is not null)
            {
                var relativeDir = string.IsNullOrEmpty(relativeNs)
                    ? ""
                    : relativeNs.Replace('.', '/') + "/";
                return $"{_baseDir}{relativeDir}{fileName}";
            }
        }

        return null;
    }

    /// <summary>
    /// Looks up source info for a member within a type by matching the type name and member name.
    /// When <paramref name="parameterNames"/> is provided, attempts to match a specific overload
    /// by comparing parameter names from PE metadata.
    /// <para>
    /// Returns an exact line range only when a single method is confidently matched (unique
    /// parameter-name match or single method with that name). When multiple methods match
    /// ambiguously, returns a file-only result (<see cref="MethodSourceInfo.StartLine"/> == 0)
    /// so callers can fall back to a file-level link rather than a misleading range.
    /// </para>
    /// </summary>
    public MethodSourceInfo? GetMemberSource(string typeFullMetadataName, string memberName, IReadOnlyList<string>? parameterNames = null)
    {
        if (_pdbReader is null) return null;

        var normalized = NormalizeGenericName(typeFullMetadataName);
        if (!_normalizedNameToHandle.TryGetValue(normalized, out var typeHandle))
            return null;

        var typeDef = _peMetadata.GetTypeDefinition(typeHandle);
        var tname = _peMetadata.GetString(typeDef.Name);

        // Search methods — track all matches for confidence assessment
        string? file = null;
        int minLine = int.MaxValue, maxLine = 0;
        int methodMatchCount = 0;
        MethodSourceInfo? paramExactMatch = null;
        int paramMatchCount = 0;

        foreach (var methodHandle in typeDef.GetMethods())
        {
            var methodDef = _peMetadata.GetMethodDefinition(methodHandle);
            var mname = _peMetadata.GetString(methodDef.Name);

            // Match method name (constructors: .ctor → member name will be type name or ".ctor")
            bool isMatch = mname == memberName
                || (memberName == ".ctor" && mname == ".ctor")
                || (mname == ".ctor" && memberName == tname)
                || (mname.StartsWith("get_", StringComparison.Ordinal) && mname[4..] == memberName)
                || (mname.StartsWith("set_", StringComparison.Ordinal) && mname[4..] == memberName)
                || (mname.StartsWith("add_", StringComparison.Ordinal) && mname[4..] == memberName)
                || (mname.StartsWith("remove_", StringComparison.Ordinal) && mname[7..] == memberName);

            if (!isMatch) continue;

            var rowId = MetadataTokens.GetRowNumber(methodHandle);
            if (!_methodSources.TryGetValue(rowId, out var info)) continue;

            methodMatchCount++;

            // Track parameter-name matches for overload resolution
            if (parameterNames is not null && ParameterNamesMatch(methodDef, parameterNames))
            {
                paramExactMatch = info;
                paramMatchCount++;
            }

            // Accumulate for merged fallback range
            file ??= info.File;
            if (info.StartLine < minLine) minLine = info.StartLine;
            if (info.EndLine > maxLine) maxLine = info.EndLine;
        }

        // Also search fields (for const/static fields)
        foreach (var fieldHandle in typeDef.GetFields())
        {
            var fieldDef = _peMetadata.GetFieldDefinition(fieldHandle);
            var fname = _peMetadata.GetString(fieldDef.Name);
            if (fname != memberName) continue;

            // Fields don't have sequence points — return file-only (no line range).
            if (_typeSources.TryGetValue(MetadataTokens.GetRowNumber(typeHandle), out var typeInfo))
            {
                return new MethodSourceInfo(typeInfo.File, 0, 0);
            }
        }

        // Check compiler-generated nested types (async state machines, iterators)
        // whose names follow the pattern <MemberName>d__N or similar.
        if (file is null)
        {
            foreach (var nestedHandle in typeDef.GetNestedTypes())
            {
                var nestedType = _peMetadata.GetTypeDefinition(nestedHandle);
                var nestedName = _peMetadata.GetString(nestedType.Name);

                if (!nestedName.StartsWith($"<{memberName}>", StringComparison.Ordinal))
                    continue;

                foreach (var nestedMethod in nestedType.GetMethods())
                {
                    var rowId = MetadataTokens.GetRowNumber(nestedMethod);
                    if (_methodSources.TryGetValue(rowId, out var info))
                    {
                        methodMatchCount++;
                        file ??= info.File;
                        if (info.StartLine < minLine) minLine = info.StartLine;
                        if (info.EndLine > maxLine) maxLine = info.EndLine;
                    }
                }
            }
        }

        // Return based on confidence level:
        // 1. Unique parameter-name match → confident
        if (paramExactMatch is not null && paramMatchCount == 1)
        {
            return paramExactMatch;
        }

        // 2. Single method matched by name (no overloads) → confident
        if (methodMatchCount == 1 && file is not null)
        {
            return new MethodSourceInfo(file, minLine, maxLine);
        }

        // 3. Multiple methods or ambiguous match → return file only (no line range)
        if (file is not null)
        {
            return new MethodSourceInfo(file, 0, 0);
        }

        return null;
    }

    /// <summary>
    /// Checks whether a method definition's parameter names match the expected names exactly.
    /// Skips sequence number 0 (the return value pseudo-parameter).
    /// </summary>
    private bool ParameterNamesMatch(MethodDefinition methodDef, IReadOnlyList<string> expectedNames)
    {
        var actualNames = new List<string>();
        foreach (var paramHandle in methodDef.GetParameters())
        {
            var param = _peMetadata.GetParameter(paramHandle);
            if (param.SequenceNumber > 0)
            {
                actualNames.Add(_peMetadata.GetString(param.Name));
            }
        }

        if (actualNames.Count != expectedNames.Count) return false;
        for (int i = 0; i < actualNames.Count; i++)
        {
            if (actualNames[i] != expectedNames[i]) return false;
        }
        return true;
    }

    /// <summary>
    /// Strips the deterministic build path prefix (e.g. "/_/") from source paths.
    /// </summary>
    internal static string CleanPath(string path)
    {
        var normalizedPath = path.Replace('\\', '/');
        if (normalizedPath.StartsWith("/_/", StringComparison.Ordinal))
        {
            return normalizedPath[3..];
        }

        var sourceIndex = normalizedPath.LastIndexOf("/src/", StringComparison.OrdinalIgnoreCase);
        if (sourceIndex >= 0)
        {
            return normalizedPath[(sourceIndex + 1)..];
        }

        if (Path.IsPathRooted(path) ||
            (normalizedPath.Length > 2 && char.IsLetter(normalizedPath[0]) && normalizedPath[1] == ':'))
        {
            return Path.GetFileName(normalizedPath);
        }

        return normalizedPath;
    }

    public void Dispose()
    {
        _pdbProvider?.Dispose();
        _peReader.Dispose();
    }
}

/// <summary>
/// Source file and line range info.
/// When <see cref="StartLine"/> is 0, the range is unknown and only the file path is meaningful.
/// </summary>
internal sealed record MethodSourceInfo(string File, int StartLine, int EndLine)
{
    /// <summary>Returns a GitHub-friendly line range like "15-200".</summary>
    public string ToLineRange() => $"{StartLine}-{EndLine}";
}
