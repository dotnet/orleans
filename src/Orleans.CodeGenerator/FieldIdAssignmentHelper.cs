#nullable enable
using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO.Hashing;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal class FieldIdAssignmentHelper
{
    private readonly GenerateFieldIds _implicitMemberSelectionStrategy;
    private readonly ImmutableArray<IParameterSymbol> _constructorParameters;
    private readonly LibraryTypes _libraryTypes;
    private readonly ISymbol[] _memberSymbols;
    private readonly Dictionary<ISymbol, (ushort, bool)> _symbols = new(SymbolEqualityComparer.Default);

    public bool IsValidForSerialization { get; }
    public string? FailureReason { get; private set; }
    public IEnumerable<ISymbol> Members => _memberSymbols;

    public FieldIdAssignmentHelper(INamedTypeSymbol typeSymbol, ImmutableArray<IParameterSymbol> constructorParameters,
        GenerateFieldIds implicitMemberSelectionStrategy, LibraryTypes libraryTypes)
    {
        _constructorParameters = constructorParameters;
        _implicitMemberSelectionStrategy = implicitMemberSelectionStrategy;
        _libraryTypes = libraryTypes;
        _memberSymbols = GetMembers(typeSymbol).ToArray();

        IsValidForSerialization = _implicitMemberSelectionStrategy != GenerateFieldIds.None && !HasMemberWithIdAnnotation() ? GenerateImplicitFieldIds() : ExtractFieldIdAnnotations();
    }

    public bool TryGetSymbolKey(ISymbol symbol, out (ushort, bool) key) => _symbols.TryGetValue(symbol, out key);

    private bool HasMemberWithIdAnnotation() => _memberSymbols.Any(member => _libraryTypes.IdAttributeTypes.Any(member.HasAttribute));

    private IEnumerable<ISymbol> GetMembers(INamedTypeSymbol symbol)
    {
        foreach (var member in symbol.GetMembers().OrderBy(m => m.MetadataName))
        {
            if (member.IsStatic || member.IsAbstract)
            {
                continue;
            }

            if (member is not (IFieldSymbol or IPropertySymbol))
            {
                continue;
            }

            if (member.HasAttribute(_libraryTypes.NonSerializedAttribute))
            {
                continue;
            }

            yield return member;
        }
    }

    private bool ExtractFieldIdAnnotations()
    {
        foreach (var member in _memberSymbols)
        {
            if (member is IPropertySymbol prop)
            {
                var id = CodeGenerator.GetId(_libraryTypes, prop);

                if (id.HasValue)
                {
                    _symbols[member] = (id.Value, false);
                }
            }

            if (member is IFieldSymbol field)
            {
                var id = CodeGenerator.GetId(_libraryTypes, field);
                var isConstructorParameter = false;

                if (!id.HasValue)
                {
                    var property = GetMatchingProperty(field);
                    if (property is null)
                    {
                        continue;
                    }

                    id = CodeGenerator.GetId(_libraryTypes, property);
                    if (!id.HasValue)
                    {
                        var constructorParameter = _constructorParameters.FirstOrDefault(x => x.Name.Equals(property.Name, StringComparison.OrdinalIgnoreCase));
                        if (constructorParameter is not null)
                        {
                            id = (ushort)_constructorParameters.IndexOf(constructorParameter);
                            isConstructorParameter = true;
                        }
                    }
                }

                if (id.HasValue)
                {
                    _symbols[member] = (id.Value, isConstructorParameter);
                }
            }
        }
        return true;
    }

    private IPropertySymbol? GetMatchingProperty(IFieldSymbol field)
    {
        var propertyName = Regex.Match(field.Name, "^<([^>]+)>.*$");
        if (!propertyName.Success)
        {
            return null;
        }

        var name = propertyName.Groups[1].Value;
        var candidates = _memberSymbols.OfType<IPropertySymbol>()
            .Where(property => string.Equals(name, property.Name, StringComparison.Ordinal)
                               && SymbolEqualityComparer.Default.Equals(field.Type, property.Type)).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private IFieldSymbol? GetMatchingField(IPropertySymbol property)
    {
        var backingFieldName = $"<{property.Name}>k__BackingField";
        var candidates = (from field in _memberSymbols.OfType<IFieldSymbol>()
                          where SymbolEqualityComparer.Default.Equals(field.Type, property.Type)
                          where field.Name == backingFieldName || GetCanonicalName(field.Name) == GetCanonicalName(property.Name)
                          select field).ToArray();
        return candidates.Length == 1 ? candidates[0] : null;
    }

    private string GetCanonicalName(string name)
    {
        name = name.TrimStart('_');
        if (name.Length > 0 && char.IsUpper(name[0]))
            name = $"{char.ToLowerInvariant(name[0])}{name.Substring(1)}";
        return name;
    }

    private (string, ushort) GetCanonicalNameAndFieldId(ITypeSymbol typeSymbol, string name)
    {
        name = GetCanonicalName(name);

        // compute the hash from the type name (without namespace, to allow it to move around) and name
        var typeName = typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);
        var hashCode = ComputeHash($"{typeName}%{name}");
        return (name, (ushort)hashCode);

        static unsafe uint ComputeHash(string data)
        {
            uint hash;
            var input = BitConverter.IsLittleEndian ? MemoryMarshal.AsBytes(data.AsSpan()) : Encoding.Unicode.GetBytes(data);
            XxHash32.TryHash(input, new Span<byte>((byte*)&hash, sizeof(uint)), out _);
            return BitConverter.IsLittleEndian ? hash : BinaryPrimitives.ReverseEndianness(hash);
        }
    }

    private bool GenerateImplicitFieldIds()
    {
        var constructorFieldIds = new Dictionary<string, ushort>();
        foreach (var parameter in _constructorParameters)
        {
            var (canonicalName, fieldId) = GetCanonicalNameAndFieldId(parameter.Type, parameter.Name);
            constructorFieldIds[canonicalName] = fieldId;
        }

        var success = _implicitMemberSelectionStrategy switch
        {
            GenerateFieldIds.PublicProperties => GenerateFromProperties(_memberSymbols.OfType<IPropertySymbol>()),
            _ => false
        };

        // validate - we can only use generated field ids if there were no collisions
        if (success && _symbols.Values.Distinct().Count() != _symbols.Count)
        {
            FailureReason = "hash collision (consider using explicit [Id] annotations for this type)";
            return false;
        }

        return success;

        bool GenerateFromProperties(IEnumerable<IPropertySymbol> properties)
        {
            foreach (var property in properties)
            {
                var (canonicalName, fieldId) = GetCanonicalNameAndFieldId(property.Type, property.Name);
                var isConstructorParameter = constructorFieldIds.TryGetValue(canonicalName, out var constructorFieldId);

                // abort when inconsistencies are detected
                if (isConstructorParameter && fieldId != constructorFieldId)
                {
                    FailureReason = $"type mismatch for property {property.Name} and its corresponding constructor parameter";
                    return false;
                }

                // for immutable types we must currently use the backing field of the public property, as the serialization
                // engine does not call a custom constructor to recreate the instance
                var mustUseField = property.SetMethod == null || property.IsReadOnly
                                                              || property.SetMethod.IsInitOnly
                                                              || property.IsStatic
                                                              || property.SetMethod.IsAbstract;
                ISymbol? symbol = mustUseField ? GetMatchingField(property) : property;
                if (symbol == null)
                    continue;

                _symbols[symbol] = (fieldId, isConstructorParameter);
            }
            return _symbols.Count > 0;
        }
    }
}