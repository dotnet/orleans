using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Orleans.CodeGenerator;

internal class MetadataModel
{
    public Dictionary<INamedTypeSymbol, ProxyInterfaceDescription> InvokableInterfaces { get; } = new(SymbolEqualityComparer.Default);
    public Dictionary<InvokableMethodId, GeneratedInvokableDescription> GeneratedInvokables { get; } = new();
    public Dictionary<ISerializableTypeDescription, TypeSyntax> DefaultCopiers { get; } = new();
    internal Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, INamedTypeSymbol>> ProxyBaseTypeInvokableBaseTypes { get; } = new (SymbolEqualityComparer.Default);
}
internal readonly struct CompoundTypeAliasComponent : IEquatable<CompoundTypeAliasComponent>
{
    private readonly Either<string, ITypeSymbol> _value;
    public CompoundTypeAliasComponent(string value) => _value = new Either<string, ITypeSymbol>(value);
    public CompoundTypeAliasComponent(ITypeSymbol value) => _value = new Either<string, ITypeSymbol>(value);
    public static CompoundTypeAliasComponent Default => new();
    public bool IsDefault => _value.RawValue is null;
    public bool IsString => _value.IsLeft;
    public string? StringValue => _value.LeftValue;
    public bool IsType => _value.IsRight;
    public ITypeSymbol? TypeValue => _value.RightValue;
    public object? Value => _value.RawValue;

    public bool Equals(CompoundTypeAliasComponent other) => (Value, other.Value) switch
    {
        (null, null) => true,
        (string stringValue, string otherStringValue) => string.Equals(stringValue, otherStringValue),
        (ITypeSymbol typeValue, ITypeSymbol otherTypeValue) => SymbolEqualityComparer.Default.Equals(typeValue, otherTypeValue),
        _ => false,
    };
    public override bool Equals(object? obj) => obj is CompoundTypeAliasComponent other && Equals(other);
    public override int GetHashCode() => _value.RawValue switch
    {
        string stringValue => stringValue.GetHashCode(),
        ITypeSymbol type => SymbolEqualityComparer.Default.GetHashCode(type),
        _ => throw new InvalidOperationException($"Unsupported type {_value.RawValue}")
    };

    internal readonly struct EqualityComparer : IEqualityComparer<CompoundTypeAliasComponent>
    {
        public static EqualityComparer Default => default;
        public bool Equals(CompoundTypeAliasComponent x, CompoundTypeAliasComponent y) => x.Equals(y);
        public int GetHashCode(CompoundTypeAliasComponent obj) => obj.GetHashCode();
    }

    public override string ToString() => _value.RawValue?.ToString() ?? string.Empty;
}

internal readonly struct Either<T, U> where T : class where U : class
{
    public Either(T value)
    {
        RawValue = value;
        IsLeft = true;
    }

    public Either(U value)
    {
        RawValue = value;
        IsLeft = false;
    }

    public bool IsLeft { get; }
    public bool IsRight => !IsLeft;
    public T? LeftValue => (T?)RawValue;
    public U? RightValue => (U?)RawValue;
    public object? RawValue { get; }
}
