using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

/// <summary>
/// Carries producer-selected service dependencies through reference assembly emission.
/// Version 1 uses counted sets and length-prefixed assembly/type identities, with structural generic and array shapes.
/// </summary>
/// <remarks>
/// A payload is version;arity;target, followed by counted dependency and canonical-array sets.
/// Type shapes are p(ordinal;), v(element), a(rank;element), and n(length:assembly,length:name,arity;arguments).
/// A well-formed entry, including zero counts, asserts completeness. Absent entries retain legacy discovery.
/// </remarks>
internal static class SerializerDependencyContract
{
    internal const string KeyPrefix = "Orleans.CodeGenerator.DependencyContract:";
    private const int Version = 1;
    private const int MaximumTypes = 1024;
    private const int MaximumDepth = 128;

    internal static string? CreateAttribute(
        ISerializableTypeDescription type, IEnumerable<ITypeSymbol> serviceDependencies, string implementationName)
    {
        // Static contexts reject these models before examining their implementation dependencies.
        // In particular, invokables have a complex base and do not need a context dependency contract.
        if (type.IsAbstractType || !SerializerFactoryGenerator.HasSupportedModelConstruction(type)
            || type is not SerializableTypeDescription model)
        {
            return null;
        }

        var parameters = type.TypeParameters.Select(static parameter => parameter.Parameter).ToArray();
        var dependencies = serviceDependencies.ToArray();
        var value = new StringBuilder();
        WriteNumber(value, Version);
        WriteNumber(value, parameters.Length);
        WriteType(value, model.Type, parameters, type.Compilation);
        WriteSet(dependencies);
        WriteSet(dependencies.OfType<IArrayTypeSymbol>().Where(static array => array.IsSZArray));
        var name = $"{type.GeneratedNamespace}.{implementationName}"
            + (parameters.Length > 0 ? $"`{parameters.Length}" : string.Empty);
        return $"[assembly: global::System.Reflection.AssemblyMetadataAttribute({CodeText.StringLiteral(KeyPrefix + name)}, {CodeText.StringLiteral(value.ToString())})]";

        void WriteSet(IEnumerable<ITypeSymbol> types)
        {
            var shapes = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var dependency in types)
            {
                var shape = new StringBuilder();
                WriteType(shape, dependency, parameters, type.Compilation);
                shapes.Add(shape.ToString());
            }

            WriteNumber(value, shapes.Count);
            foreach (var shape in shapes)
            {
                value.Append(shape);
            }
        }
    }

    private static void WriteType(StringBuilder result, ITypeSymbol type, ITypeParameterSymbol[] parameters, Compilation compilation)
    {
        switch (type)
        {
            case ITypeParameterSymbol parameter:
                var ordinal = Array.FindIndex(parameters, candidate => SymbolEqualityComparer.Default.Equals(candidate, parameter));
                if (ordinal < 0)
                {
                    throw new InvalidOperationException($"Dependency parameter '{parameter.Name}' is not declared by its implementation.");
                }

                result.Append('p');
                WriteNumber(result, ordinal);
                break;
            case IArrayTypeSymbol array:
                result.Append(array.IsSZArray ? 'v' : 'a');
                if (!array.IsSZArray)
                {
                    WriteNumber(result, array.Rank);
                }

                WriteType(result, array.ElementType, parameters, compilation);
                break;
            case IDynamicTypeSymbol:
                WriteType(result, compilation.GetSpecialType(SpecialType.System_Object), parameters, compilation);
                break;
            case INamedTypeSymbol named:
                if (named.IsTupleType && named.TupleUnderlyingType is { } tuple)
                {
                    named = tuple;
                }

                var identity = TypeMetadataIdentity.Create(named);
                result.Append('n');
                WriteString(result, identity.AssemblyIdentity);
                WriteString(result, identity.MetadataName);
                var arguments = named.GetAllTypeArguments().ToArray();
                WriteNumber(result, arguments.Length);
                foreach (var argument in arguments)
                {
                    WriteType(result, argument, parameters, compilation);
                }

                break;
            default:
                throw new InvalidOperationException($"Unsupported serialization dependency kind '{type.TypeKind}'.");
        }
    }

    private static void WriteNumber(StringBuilder result, int value)
        => result.Append(value.ToString(CultureInfo.InvariantCulture)).Append(';');

    private static void WriteString(StringBuilder result, string value)
        => result.Append(value.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(value);

    /// <summary>Indexes each requested producer once for a context's compilation, never across compilations.</summary>
    internal sealed class Index(Compilation compilation)
    {
        private readonly Dictionary<IAssemblySymbol, Dictionary<string, string?>> _assemblies = new(SymbolEqualityComparer.Default);

        internal bool TryRead(
            INamedTypeSymbol implementation, INamedTypeSymbol target,
            List<ITypeSymbol> dependencies, List<IArrayTypeSymbol> canonicalArrays, out string? failure)
        {
            var producer = implementation.ContainingAssembly;
            if (!_assemblies.TryGetValue(producer, out var entries))
            {
                entries = new(StringComparer.Ordinal);
                foreach (var attribute in producer.GetAttributes())
                {
                    if (attribute.AttributeClass?.ToDisplayString() != "System.Reflection.AssemblyMetadataAttribute"
                        || attribute.ConstructorArguments.Length != 2
                        || attribute.ConstructorArguments[0].Value is not string key
                        || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var name = key.Substring(KeyPrefix.Length);
                    if (entries.ContainsKey(name))
                    {
                        entries[name] = null;
                    }
                    else
                    {
                        entries.Add(name, attribute.ConstructorArguments[1].Value as string);
                    }
                }

                _assemblies.Add(producer, entries);
            }

            var metadataName = TypeMetadataIdentity.Create(implementation).MetadataName;
            if (!entries.TryGetValue(metadataName, out var payload))
            {
                failure = null;
                return false;
            }

            if (payload is null)
            {
                failure = "the producer dependency contract is duplicated or has an invalid value";
                return true;
            }

            try
            {
                new Reader(compilation, producer, implementation, target, payload).Read(dependencies, canonicalArrays);
                failure = null;
            }
            catch (FormatException exception)
            {
                failure = $"invalid producer dependency contract: {exception.Message}";
            }

            return true;
        }
    }

    private sealed class Reader(
        Compilation compilation, IAssemblySymbol producer, INamedTypeSymbol implementation, INamedTypeSymbol target, string payload)
    {
        private readonly ITypeSymbol[] _arguments = target.GetAllTypeArguments().ToArray();
        private readonly HashSet<ITypeSymbol> _types = new(SymbolEqualityComparer.Default);
        private int _position;

        public void Read(List<ITypeSymbol> dependencies, List<IArrayTypeSymbol> canonicalArrays)
        {
            if (ReadNumber(';') != Version)
            {
                throw new FormatException("unsupported version");
            }

            var arity = ReadNumber(';');
            if (arity != _arguments.Length || arity != implementation.GetAllTypeArguments().Count())
            {
                throw new FormatException("implementation generic arity does not match");
            }

            if (!SymbolEqualityComparer.Default.Equals(ReadType(0), target))
            {
                throw new FormatException("target identity does not match the implementation");
            }

            var dependencyCount = ReadCount();
            for (var index = 0; index < dependencyCount; index++)
            {
                dependencies.Add(ReadType(0));
            }

            var arrayCount = ReadCount();
            for (var index = 0; index < arrayCount; index++)
            {
                if (ReadType(0) is not IArrayTypeSymbol { IsSZArray: true } array)
                {
                    throw new FormatException("canonical services require vector array shapes");
                }

                canonicalArrays.Add(array);
                dependencies.Add(array);
                dependencies.Add(array.ElementType);
            }

            if (_position != payload.Length)
            {
                throw new FormatException("unexpected trailing data");
            }
        }

        private ITypeSymbol ReadType(int depth)
        {
            if (depth >= MaximumDepth || _position >= payload.Length)
            {
                throw new FormatException("type graph is truncated or exceeds its limits");
            }

            ITypeSymbol result;
            switch (payload[_position++])
            {
                case 'p':
                    var ordinal = ReadNumber(';');
                    if (ordinal >= _arguments.Length)
                    {
                        throw new FormatException("generic parameter ordinal is out of range");
                    }

                    result = _arguments[ordinal];
                    break;
                case 'v':
                    result = compilation.CreateArrayTypeSymbol(ReadType(depth + 1));
                    break;
                case 'a':
                    var rank = ReadNumber(';');
                    if (rank is < 2 or > 32)
                    {
                        throw new FormatException("unsupported non-vector array rank");
                    }

                    result = compilation.CreateArrayTypeSymbol(ReadType(depth + 1), rank);
                    break;
                case 'n':
                    var assemblyIdentity = ReadString();
                    var metadataName = ReadString();
                    if (metadataName.Length == 0)
                    {
                        throw new FormatException("named type identity is empty");
                    }

                    var definition = ResolveAssembly(assemblyIdentity) is { } assembly
                        ? assembly.GetTypeByMetadataName(metadataName) ?? assembly.ResolveForwardedType(metadataName)
                        : null;
                    if (definition is null || definition.TypeKind == TypeKind.Error)
                    {
                        throw new FormatException($"required type '{metadataName}' from '{assemblyIdentity}' is unavailable");
                    }

                    if (!SymbolEqualityComparer.Default.Equals(compilation.GetTypeByMetadataName(metadataName), definition))
                    {
                        throw new FormatException($"required dependency '{metadataName}' cannot be named unambiguously in the generated context");
                    }

                    var arity = GetArity(definition);
                    var argumentCount = ReadCount();
                    if (argumentCount != arity)
                    {
                        throw new FormatException("named type generic arity does not match");
                    }

                    var arguments = new ITypeSymbol[argumentCount];
                    for (var index = 0; index < argumentCount; index++)
                    {
                        arguments[index] = ReadType(depth + 1);
                    }

                    var offset = 0;
                    result = Construct(definition, arguments, ref offset);
                    break;
                default:
                    throw new FormatException("unknown type shape");
            }

            if (!compilation.IsSymbolAccessibleWithin(result, compilation.Assembly))
            {
                throw new FormatException($"required dependency '{result}' is inaccessible to the generated context");
            }

            // Match the context's existing closed-type/metadata budget, not the number of repeated shape nodes.
            if (_types.Add(result) && _types.Count > MaximumTypes)
            {
                throw new FormatException("closed dependency types exceed the context's 1024-type limit");
            }

            return result;
        }

        private static int GetArity(INamedTypeSymbol definition)
        {
            var arity = 0;
            var depth = 0;
            for (var current = definition; current is not null; current = current.ContainingType)
            {
                if (depth++ >= MaximumDepth)
                {
                    throw new FormatException("containing type nesting exceeds the context's 128-level limit");
                }

                arity += current.Arity;
            }

            return arity;
        }

        private static INamedTypeSymbol Construct(INamedTypeSymbol definition, ITypeSymbol[] arguments, ref int offset, int depth = 0)
        {
            if (depth >= MaximumDepth)
            {
                throw new FormatException("containing type nesting exceeds the context's 128-level limit");
            }

            if (definition.ContainingType is { } containing)
            {
                definition = Construct(containing, arguments, ref offset, depth + 1).GetTypeMembers(definition.Name, definition.Arity).Single();
            }

            if (definition.Arity == 0)
            {
                return definition;
            }

            var ownArguments = arguments.Skip(offset).Take(definition.Arity).ToArray();
            offset += definition.Arity;
            return definition.Construct(ownArguments);
        }

        private IAssemblySymbol? ResolveAssembly(string displayName)
        {
            if (!AssemblyIdentity.TryParseDisplayName(displayName, out var identity))
            {
                throw new FormatException("invalid assembly identity");
            }

            // The producer module records the original reference identity alongside the compiler's
            // resolved/retargeted symbol. Follow that mapping instead of guessing by simple type name.
            if (producer.Identity.Equals(identity))
            {
                return FindCompilationAssembly(producer.Identity);
            }

            foreach (var module in producer.Modules)
            {
                for (var index = 0; index < module.ReferencedAssemblies.Length; index++)
                {
                    if (module.ReferencedAssemblies[index].Equals(identity))
                    {
                        return FindCompilationAssembly(module.ReferencedAssemblySymbols[index].Identity);
                    }
                }
            }

            return FindCompilationAssembly(identity);
        }

        private IAssemblySymbol? FindCompilationAssembly(AssemblyIdentity identity)
        {
            if (compilation.Assembly.Identity.Equals(identity))
            {
                return compilation.Assembly;
            }

            return compilation.SourceModule.ReferencedAssemblySymbols.FirstOrDefault(assembly => assembly.Identity.Equals(identity));
        }

        private int ReadCount()
        {
            var count = ReadNumber(';');
            return count <= payload.Length - _position
                ? count
                : throw new FormatException("declared type count exceeds the remaining payload");
        }

        private int ReadNumber(char delimiter)
        {
            var start = _position;
            var result = 0;
            while (_position < payload.Length && payload[_position] != delimiter)
            {
                var digit = payload[_position++] - '0';
                if ((uint)digit > 9 || result > (int.MaxValue - digit) / 10)
                {
                    throw new FormatException("invalid or overflowing count");
                }

                result = result * 10 + digit;
            }

            if (_position == start || _position >= payload.Length)
            {
                throw new FormatException("invalid or truncated count");
            }

            _position++;
            return result;
        }

        private string ReadString()
        {
            var length = ReadNumber(':');
            if (length > payload.Length - _position)
            {
                throw new FormatException("truncated identity");
            }

            var result = payload.Substring(_position, length);
            _position += length;
            return result;
        }
    }
}
