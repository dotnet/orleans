using System.Collections.Immutable;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Text;
using Orleans.CodeGenerator.Model;

namespace Orleans.CodeGenerator;

internal readonly struct GeneratedSourceEntry(string hintName, string source) : IEquatable<GeneratedSourceEntry>
{
    public string HintName { get; } = hintName;
    public string Source { get; } = source;
    public SourceText SourceText => SourceText.From(Source ?? string.Empty, Encoding.UTF8);

    public bool Equals(GeneratedSourceEntry other)
        => string.Equals(HintName, other.HintName, StringComparison.Ordinal)
            && string.Equals(Source, other.Source, StringComparison.Ordinal);

    public override bool Equals(object obj) => obj is GeneratedSourceEntry other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(HintName ?? string.Empty);
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(Source ?? string.Empty);
            return hash;
        }
    }
}

internal readonly struct SourceOutputResult(GeneratedSourceEntry? sourceEntry, Diagnostic? diagnostic) : IEquatable<SourceOutputResult>
{
    public GeneratedSourceEntry? SourceEntry { get; } = sourceEntry;
    public Diagnostic? Diagnostic { get; } = diagnostic;

    public static SourceOutputResult FromSource(GeneratedSourceEntry sourceEntry) => new(sourceEntry, null);
    public static SourceOutputResult FromDiagnostic(Diagnostic diagnostic) => new(null, diagnostic);

    public bool Equals(SourceOutputResult other)
        => Nullable.Equals(SourceEntry, other.SourceEntry)
            && SourceGeneratorDiagnosticComparer.AreEqual(Diagnostic, other.Diagnostic);

    public override bool Equals(object? obj) => obj is SourceOutputResult other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = SourceEntry.GetHashCode();
            hash = hash * 31 + SourceGeneratorDiagnosticComparer.GetHashCode(Diagnostic);
            return hash;
        }
    }
}

internal readonly struct ReferenceAssemblyDataResult(ReferenceAssemblyModel model, ImmutableArray<Diagnostic> diagnostics) : IEquatable<ReferenceAssemblyDataResult>
{
    public ReferenceAssemblyModel Model { get; } = model;
    public ImmutableArray<Diagnostic> Diagnostics { get; } = diagnostics.IsDefault ? [] : diagnostics;

    public static ReferenceAssemblyDataResult FromModelAndDiagnostics(ReferenceAssemblyModel model, ImmutableArray<Diagnostic> diagnostics)
        => new(model, diagnostics);

    public bool Equals(ReferenceAssemblyDataResult other)
        => EqualityComparer<ReferenceAssemblyModel>.Default.Equals(Model, other.Model)
            && SourceGeneratorDiagnosticComparer.AreSequencesEqual(Diagnostics, other.Diagnostics);

    public override bool Equals(object? obj) => obj is ReferenceAssemblyDataResult other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Model?.GetHashCode() ?? 0;
            hash = hash * 31 + SourceGeneratorDiagnosticComparer.GetSequenceHashCode(Diagnostics);
            return hash;
        }
    }
}

internal readonly struct SerializableTypeResult(
    SerializableTypeModel? model,
    Diagnostic? diagnostic,
    TypeMetadataIdentity metadataIdentity,
    SourceLocationModel sourceLocation,
    string typeSyntax) : IEquatable<SerializableTypeResult>
{
    public SerializableTypeModel? Model { get; } = model;
    public Diagnostic? Diagnostic { get; } = diagnostic;
    public TypeMetadataIdentity MetadataIdentity { get; } = metadataIdentity;
    public SourceLocationModel SourceLocation { get; } = sourceLocation;
    public string TypeSyntax { get; } = typeSyntax;

    public static SerializableTypeResult FromModel(SerializableTypeModel model)
        => new(
            model,
            diagnostic: null,
            model?.MetadataIdentity ?? TypeMetadataIdentity.Empty,
            model?.SourceLocation ?? default,
            model?.TypeSyntax.SyntaxString ?? string.Empty);

    public static SerializableTypeResult FromDiagnostic(
        Diagnostic diagnostic,
        TypeMetadataIdentity metadataIdentity,
        SourceLocationModel sourceLocation,
        string typeSyntax)
        => new(model: null, diagnostic, metadataIdentity, sourceLocation, typeSyntax ?? string.Empty);

    public bool Equals(SerializableTypeResult other)
        => Nullable.Equals(Model, other.Model)
            && SourceGeneratorDiagnosticComparer.AreEqual(Diagnostic, other.Diagnostic)
            && MetadataIdentity.Equals(other.MetadataIdentity)
            && SourceLocation.Equals(other.SourceLocation)
            && string.Equals(TypeSyntax, other.TypeSyntax, StringComparison.Ordinal);

    public override bool Equals(object? obj) => obj is SerializableTypeResult other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = Model?.GetHashCode() ?? 0;
            hash = hash * 31 + SourceGeneratorDiagnosticComparer.GetHashCode(Diagnostic);
            hash = hash * 31 + MetadataIdentity.GetHashCode();
            hash = hash * 31 + SourceLocation.GetHashCode();
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(TypeSyntax ?? string.Empty);
            return hash;
        }
    }
}

internal readonly struct ProxyOutputPreparationResult(
    ImmutableArray<ProxyOutputModel> proxyOutputModels,
    ImmutableArray<SourceOutputResult> sourceOutputs,
    Diagnostic? diagnostic) : IEquatable<ProxyOutputPreparationResult>
{
    public ImmutableArray<ProxyOutputModel> ProxyOutputModels { get; } = proxyOutputModels;
    public ImmutableArray<SourceOutputResult> SourceOutputs { get; } = sourceOutputs;
    public Diagnostic? Diagnostic { get; } = diagnostic;

    public static ProxyOutputPreparationResult FromModelsAndSources(
        ImmutableArray<ProxyOutputModel> proxyOutputModels,
        ImmutableArray<SourceOutputResult> sourceOutputs)
        => new(proxyOutputModels, sourceOutputs, diagnostic: null);

    public static ProxyOutputPreparationResult FromDiagnostic(Diagnostic diagnostic)
        => new([], [], diagnostic);

    public bool Equals(ProxyOutputPreparationResult other)
        => StructuralEquality.SequenceEqual(ProxyOutputModels, other.ProxyOutputModels)
            && StructuralEquality.SequenceEqual(SourceOutputs, other.SourceOutputs)
            && SourceGeneratorDiagnosticComparer.AreEqual(Diagnostic, other.Diagnostic);

    public override bool Equals(object? obj) => obj is ProxyOutputPreparationResult other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            var hash = StructuralEquality.GetSequenceHashCode(ProxyOutputModels);
            hash = hash * 31 + StructuralEquality.GetSequenceHashCode(SourceOutputs);
            hash = hash * 31 + SourceGeneratorDiagnosticComparer.GetHashCode(Diagnostic);
            return hash;
        }
    }
}

internal static class SourceGeneratorDiagnosticComparer
{
    internal static bool AreSequencesEqual(ImmutableArray<Diagnostic> left, ImmutableArray<Diagnostic> right)
    {
        if (left.IsDefaultOrEmpty)
        {
            return right.IsDefaultOrEmpty;
        }

        if (right.IsDefaultOrEmpty || left.Length != right.Length)
        {
            return false;
        }

        for (var i = 0; i < left.Length; i++)
        {
            if (!AreEqual(left[i], right[i]))
            {
                return false;
            }
        }

        return true;
    }

    internal static int GetSequenceHashCode(ImmutableArray<Diagnostic> diagnostics)
    {
        if (diagnostics.IsDefaultOrEmpty)
        {
            return 0;
        }

        unchecked
        {
            var hash = 0;
            foreach (var diagnostic in diagnostics)
            {
                hash = hash * 31 + GetHashCode(diagnostic);
            }

            return hash;
        }
    }

    internal static bool AreEqual(Diagnostic? left, Diagnostic? right)
    {
        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        return string.Equals(left.Id, right.Id, StringComparison.Ordinal)
            && left.Severity == right.Severity
            && left.WarningLevel == right.WarningLevel
            && string.Equals(left.ToString(), right.ToString(), StringComparison.Ordinal);
    }

    internal static int GetHashCode(Diagnostic? diagnostic)
    {
        if (diagnostic is null)
        {
            return 0;
        }

        unchecked
        {
            var hash = StringComparer.Ordinal.GetHashCode(diagnostic.Id ?? string.Empty);
            hash = hash * 31 + (int)diagnostic.Severity;
            hash = hash * 31 + diagnostic.WarningLevel;
            hash = hash * 31 + StringComparer.Ordinal.GetHashCode(diagnostic.ToString() ?? string.Empty);
            return hash;
        }
    }
}
