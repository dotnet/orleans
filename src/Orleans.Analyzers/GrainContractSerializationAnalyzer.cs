using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Orleans.Analyzers;

/// <summary>
/// Checks the statically declared data graph of grain contracts and Orleans-serialized state.
/// These strict rules are opt-in since runtime serializer and storage configuration is not available to Roslyn.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class GrainContractSerializationAnalyzer : DiagnosticAnalyzer
{
    public const string ContractRuleId = "ORLEANS0028";
    public const string StateRuleId = "ORLEANS0029";
    private const int MaxGraphDepth = 64;
    private const int MaxGraphTypes = 1024;

    private static readonly DiagnosticDescriptor ContractRule = CreateRule(ContractRuleId,
        "Grain contract data types must have serialization support");
    private static readonly DiagnosticDescriptor StateRule = CreateRule(StateRuleId,
        "Grain state data types must have serialization support");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics => [ContractRule, StateRule];

    private static DiagnosticDescriptor CreateRule(string id, string title) => new(
        id, title,
        "Serialization support for type '{0}' used by '{1}' cannot be verified: {2}",
        "Usage", DiagnosticSeverity.Warning, isEnabledByDefault: false,
        description: "Concrete data types reachable from a grain contract require generated serialization or a statically registered serializer. Runtime serializer configuration and runtime subtypes cannot be verified by this rule.",
        helpLinkUri: Constants.GetDiagnosticHelpLink(id));

    public override void Initialize(AnalysisContext context)
    {
        context.EnableConcurrentExecution();
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.RegisterCompilationStartAction(context =>
        {
            if (context.Compilation.GetTypeByMetadataName(Constants.IGrainFullyQualifiedName) is not { } grain
                || context.Compilation.GetTypeByMetadataName(Constants.GenerateSerializerAttributeFullyQualifiedName) is not { } generateSerializer)
            {
                return;
            }

            var implicitProperties = context.Options.AnalyzerConfigOptionsProvider.GlobalOptions.TryGetValue(
                "build_property.Orleans_GenerateFieldIds", out var fieldIds) && fieldIds is "PublicProperties" or "1";
            var symbols = new SerializationSymbols(context.Compilation, grain, generateSerializer, implicitProperties);
            var serializers = SerializationSupport.Create(context.Compilation, generateSerializer.ContainingAssembly, context.CancellationToken);
            context.RegisterSymbolAction(context => AnalyzeType(context, symbols, serializers), SymbolKind.NamedType);
        });
    }

    private static void AnalyzeType(SymbolAnalysisContext context, SerializationSymbols symbols, SerializationSupport serializers)
    {
        var type = (INamedTypeSymbol)context.Symbol;
        if (type.TypeKind == TypeKind.Interface && symbols.IsGrainContract(type))
        {
            AnalyzeContracts(context, [type], type, symbols, serializers);
        }
        else if (type.TypeKind == TypeKind.Class && symbols.IsGrainImplementation(type))
        {
            // Source interfaces own their diagnostics. A grain class also checks contracts supplied by another assembly.
            AnalyzeContracts(context, type.AllInterfaces.Where(contract => symbols.IsGrainContract(contract)
                && (!contract.Locations.Any(static location => location.IsInSource)
                    || !SymbolEqualityComparer.Default.Equals(contract, contract.OriginalDefinition))), type, symbols, serializers);

            AnalyzeState(context, type, symbols, serializers);
        }
    }

    private static void AnalyzeContracts(SymbolAnalysisContext context, IEnumerable<INamedTypeSymbol> contracts, INamedTypeSymbol owner,
        SerializationSymbols symbols, SerializationSupport serializers)
    {
        var methods = new HashSet<IMethodSymbol>(SymbolEqualityComparer.Default);
        foreach (var contract in contracts)
            foreach (var declaringType in contract.AllInterfaces.Prepend(contract))
            {
                if (owner.TypeKind == TypeKind.Interface && !SymbolEqualityComparer.Default.Equals(declaringType, contract)
                    && symbols.IsGrainContract(declaringType) && declaringType.Locations.Any(static location => location.IsInSource)
                    && SymbolEqualityComparer.Default.Equals(declaringType, declaringType.OriginalDefinition))
                {
                    continue;
                }

                foreach (var method in declaringType.GetMembers().OfType<IMethodSymbol>())
                {
                    if (method.MethodKind != MethodKind.Ordinary || method.IsStatic
                        || method.DeclaredAccessibility != Accessibility.Public || !methods.Add(method))
                    {
                        continue;
                    }

                    var implementation = owner.TypeKind == TypeKind.Class ? owner.FindImplementationForInterfaceMember(method) : null;
                    var fallback = implementation?.Locations.FirstOrDefault(static location => location.IsInSource)
                        ?? owner.Locations.First(static location => location.IsInSource);
                    var declaration = method.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as MethodDeclarationSyntax;
                    var returnType = method.ReturnType;
                    if (!symbols.IsReturnWrapper(returnType) || returnType is INamedTypeSymbol { TypeArguments.Length: 1 })
                    {
                        if (symbols.IsReturnWrapper(returnType) && returnType is INamedTypeSymbol { TypeArguments.Length: 1 } wrapper)
                        {
                            returnType = wrapper.TypeArguments[0];
                        }

                        AnalyzeGraph(context, returnType, declaration?.ReturnType.GetLocation() ?? fallback,
                            method, ContractRule, symbols, serializers);
                    }

                    foreach (var parameter in method.Parameters)
                    {
                        var parameterSyntax = parameter.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(context.CancellationToken) as ParameterSyntax;
                        AnalyzeGraph(context, parameter.Type, parameterSyntax?.Type?.GetLocation() ?? fallback,
                            method, ContractRule, symbols, serializers);
                    }
                }
            }
    }

    private static void AnalyzeState(SymbolAnalysisContext context, INamedTypeSymbol grain,
        SerializationSymbols symbols, SerializationSupport serializers)
    {
        var states = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        for (var type = grain; type is not null; type = type.BaseType)
        {
            if (SymbolEqualityComparer.Default.Equals(type.OriginalDefinition, symbols.StatefulGrain))
            {
                Check(type.TypeArguments[0], grain);
            }

            foreach (var member in type.GetMembers())
            {
                if (member is IMethodSymbol { MethodKind: MethodKind.Constructor } constructor)
                {
                    foreach (var parameter in constructor.Parameters)
                    {
                        CheckContainer(parameter.Type, parameter);
                    }
                }
                else if (member is IFieldSymbol { IsStatic: false } field)
                {
                    CheckContainer(field.Type, field);
                }
                else if (member is IPropertySymbol { IsStatic: false } property)
                {
                    CheckContainer(property.Type, property);
                }
            }
        }

        void CheckContainer(ITypeSymbol type, ISymbol member)
        {
            if (type is INamedTypeSymbol named)
            {
                foreach (var candidate in named.AllInterfaces.Prepend(named))
                {
                    if (SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, symbols.PersistentState))
                    {
                        Check(candidate.TypeArguments[0], member);
                    }
                }
            }
        }

        void Check(ITypeSymbol state, ISymbol member)
        {
            if (states.Add(state))
            {
                var location = member.Locations.FirstOrDefault(static location => location.IsInSource)
                    ?? grain.Locations.First(static location => location.IsInSource);
                AnalyzeGraph(context, state, location, grain, StateRule, symbols, serializers);
            }
        }
    }

    private static void AnalyzeGraph(SymbolAnalysisContext context, ITypeSymbol root, Location location, ISymbol owner,
        DiagnosticDescriptor rule, SerializationSymbols symbols, SerializationSupport serializers)
    {
        var pending = new Stack<(ITypeSymbol Type, int Depth)>();
        var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        pending.Push((root, 0));
        while (pending.Count > 0)
        {
            context.CancellationToken.ThrowIfCancellationRequested();
            var (type, depth) = pending.Pop();
            if (!visited.Add(type))
            {
                continue;
            }

            if (type is not IArrayTypeSymbol && (type is not INamedTypeSymbol candidate
                || candidate.TypeKind == TypeKind.Error || candidate.TypeKind == TypeKind.Enum || symbols.IsLeaf(candidate)))
            {
                continue;
            }

            // Check actual constructions, not just generic definitions: different arguments can select different codecs.
            // A bounded traversal also covers expanding converter chains without inventing serialization requirements
            // for unused generic arguments. Report the analysis limit instead of claiming a codec is missing.
            if (depth >= MaxGraphDepth || visited.Count >= MaxGraphTypes)
            {
                Report(root, "the serialized type graph exceeds the static traversal limit; verify serialization and suppress this warning if appropriate");
                break;
            }

            if (type is IArrayTypeSymbol array)
            {
                pending.Push((array.ElementType, depth + 1));
                continue;
            }

            var named = (INamedTypeSymbol)type;
            // Generated codecs also carry RegisterSerializer. An annotated model must still have its data graph checked.
            var attribute = symbols.GetAttribute(named, symbols.GenerateSerializer);
            if (attribute is null && serializers.TryGetSupport(named, out var inspectArguments, out var surrogate))
            {
                if (surrogate is not null)
                {
                    pending.Push((surrogate, depth + 1));
                }

                if (inspectArguments)
                {
                    PushArguments(named, depth + 1);
                }

                continue;
            }

            if (named.TypeKind == TypeKind.Interface)
            {
                // Concrete runtime implementations are unknown. Their generic data arguments are still checked.
                PushArguments(named, depth + 1);
                continue;
            }

            if (attribute is null)
            {
                Report(named, "no statically declared serializer was found; add [GenerateSerializer] or register a serializer");
                continue;
            }

            if (named.BaseType is { SpecialType: not SpecialType.System_Object and not SpecialType.System_ValueType } baseType)
            {
                pending.Push((baseType, depth + 1));
            }

            foreach (var memberType in symbols.GetSerializedMembers(named, attribute))
            {
                pending.Push((memberType, depth + 1));
            }
        }

        void Report(ITypeSymbol type, string reason) => context.ReportDiagnostic(Diagnostic.Create(rule, location,
            type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat),
            owner.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat), reason));

        void PushArguments(INamedTypeSymbol type, int depth)
        {
            foreach (var argument in type.TypeArguments)
            {
                pending.Push((argument, depth));
            }

            for (var declaringType = type.ContainingType; declaringType is not null; declaringType = declaringType.ContainingType)
            {
                foreach (var argument in declaringType.TypeArguments)
                {
                    pending.Push((argument, depth));
                }
            }
        }
    }

    private sealed class SerializationSymbols
    {
        private readonly INamedTypeSymbol _grain;
        private readonly INamedTypeSymbol? _observer;
        private readonly INamedTypeSymbol? _addressable;
        private readonly INamedTypeSymbol? _grainBase;
        private readonly INamedTypeSymbol? _id;
        private readonly INamedTypeSymbol? _nonSerialized;
        private readonly INamedTypeSymbol? _compilerGenerated;
        private readonly INamedTypeSymbol? _constructor;
        private readonly INamedTypeSymbol? _activatorConstructor;
        private readonly IAssemblySymbol _assembly;
        private readonly bool _implicitProperties;
        private readonly HashSet<INamedTypeSymbol> _leaves = new(SymbolEqualityComparer.Default);
        private readonly HashSet<INamedTypeSymbol> _returnWrappers = new(SymbolEqualityComparer.Default);

        public SerializationSymbols(Compilation compilation, INamedTypeSymbol grain, INamedTypeSymbol generateSerializer, bool implicitProperties)
        {
            _grain = grain;
            _assembly = compilation.Assembly;
            _implicitProperties = implicitProperties;
            GenerateSerializer = generateSerializer;
            _observer = compilation.GetTypeByMetadataName("Orleans.IGrainObserver");
            _addressable = compilation.GetTypeByMetadataName(Constants.IAddressibleFullyQualifiedName);
            _grainBase = compilation.GetTypeByMetadataName(Constants.IGrainBaseFullyQualifiedName);
            _id = compilation.GetTypeByMetadataName(Constants.IdAttributeFullyQualifiedName);
            _nonSerialized = compilation.GetTypeByMetadataName(Constants.NonSerializedAttributeFullyQualifiedName);
            _compilerGenerated = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.CompilerGeneratedAttribute");
            _constructor = compilation.GetTypeByMetadataName("Orleans.OrleansConstructorAttribute");
            _activatorConstructor = compilation.GetTypeByMetadataName("Microsoft.Extensions.DependencyInjection.ActivatorUtilitiesConstructorAttribute");
            StatefulGrain = compilation.GetTypeByMetadataName("Orleans.Grain`1");
            PersistentState = compilation.GetTypeByMetadataName("Orleans.Runtime.IPersistentState`1");
            foreach (var name in new[] { "System.Threading.CancellationToken", "Orleans.GrainCancellationToken" })
            {
                if (compilation.GetTypeByMetadataName(name) is { } type) _leaves.Add(type);
            }

            foreach (var name in new[] { "System.Threading.Tasks.Task", "System.Threading.Tasks.Task`1",
                "System.Threading.Tasks.ValueTask", "System.Threading.Tasks.ValueTask`1", "System.Collections.Generic.IAsyncEnumerable`1" })
            {
                if (compilation.GetTypeByMetadataName(name) is { } type) _returnWrappers.Add(type);
            }
        }

        public INamedTypeSymbol GenerateSerializer { get; }
        public INamedTypeSymbol? StatefulGrain { get; }
        public INamedTypeSymbol? PersistentState { get; }
        public bool IsGrainContract(INamedTypeSymbol type) => Implements(type, _grain) || Implements(type, _observer);
        public bool IsGrainImplementation(INamedTypeSymbol type) => IsGrainContract(type) || Implements(type, _grainBase);
        public bool IsReturnWrapper(ITypeSymbol type) => type is INamedTypeSymbol named && _returnWrappers.Contains(named.OriginalDefinition);
        public bool IsLeaf(INamedTypeSymbol type) => !type.IsGenericType && type.SpecialType != SpecialType.None
            || _leaves.Contains(type) || Implements(type, _addressable);

        private static bool Implements(INamedTypeSymbol type, INamedTypeSymbol? contract) => contract is not null
            && (SymbolEqualityComparer.Default.Equals(type, contract)
                || type.AllInterfaces.Any(candidate => SymbolEqualityComparer.Default.Equals(candidate, contract)));

        public AttributeData? GetAttribute(ISymbol symbol, INamedTypeSymbol? attribute) => symbol.GetAttributes()
            .FirstOrDefault(candidate => SymbolEqualityComparer.Default.Equals(candidate.AttributeClass, attribute));

        public IEnumerable<ITypeSymbol> GetSerializedMembers(INamedTypeSymbol type, AttributeData attribute)
        {
            // FieldIdAssignmentHelper removes excluded members before deciding whether explicit IDs disable implicit IDs.
            var members = type.GetMembers().Where(member => !member.IsStatic && !member.IsAbstract
                && member is IFieldSymbol or IPropertySymbol && GetAttribute(member, _nonSerialized) is null).ToImmutableArray();
            var constructorParameters = GetConstructorParameters(type, attribute);
            var implicitProperties = (attribute.NamedArguments.Any(static argument => argument.Key == "GenerateFieldIds" && argument.Value.Value is 1)
                    || _implicitProperties && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, _assembly))
                && !members.Any(member => GetAttribute(member, _id) is not null
                    || member is IPropertySymbol property && constructorParameters.Any(parameter =>
                        MatchesParameter(property, parameter) && GetAttribute(parameter, _id) is not null));
            foreach (var member in members)
            {
                if (member is IFieldSymbol field && GetAttribute(field, _id) is not null)
                {
                    yield return field.Type;
                }
                else if (member is IPropertySymbol { IsIndexer: false } property
                    && (GetAttribute(property, _id) is not null
                        || implicitProperties && IsImplicitlySerialized(property, members)
                        || constructorParameters.Any(parameter => MatchesParameter(property, parameter))))
                {
                    yield return property.Type;
                }
            }
        }

        private ImmutableArray<IParameterSymbol> GetConstructorParameters(INamedTypeSymbol type, AttributeData attribute)
        {
            // Match SerializableTypeModelExtractor's constructor selection, including explicit class opt-in,
            // the record primary constructor, and canonical property/parameter names.
            var include = attribute.NamedArguments.FirstOrDefault(static argument =>
                argument.Key == "IncludePrimaryConstructorParameters").Value.Value as bool?;
            var properties = type.GetMembers().OfType<IPropertySymbol>().ToArray();
            bool IsGenerated(ISymbol? symbol) => symbol is not null && GetAttribute(symbol, _compilerGenerated) is not null;
            bool IsPropertyConstructor(IMethodSymbol constructor) => constructor.Parameters.Length > 0
                && constructor.Parameters.All(parameter => properties.Any(property => property.Name == parameter.Name
                    && IsGenerated(property.GetMethod) && IsGenerated(property.SetMethod)));

            if (include is false || include is null && !type.IsRecord && !type.InstanceConstructors.Any(IsPropertyConstructor))
            {
                return [];
            }

            if (type.IsRecord)
            {
                var constructor = type.InstanceConstructors.FirstOrDefault();
                return constructor is { IsImplicitlyDeclared: false } && !IsGenerated(constructor) ? constructor.Parameters : [];
            }

            var annotated = type.InstanceConstructors.Where(constructor =>
                GetAttribute(constructor, _constructor) is not null || GetAttribute(constructor, _activatorConstructor) is not null).ToArray();
            return annotated.Length == 1 ? annotated[0].Parameters
                : type.InstanceConstructors.FirstOrDefault(IsPropertyConstructor)?.Parameters ?? [];
        }

        private static bool MatchesParameter(IPropertySymbol property, IParameterSymbol parameter) =>
            CanonicalName(parameter.Name) == CanonicalName(property.Name)
            && SymbolEqualityComparer.Default.Equals(parameter.Type, property.Type);

        private bool IsImplicitlySerialized(IPropertySymbol property, ImmutableArray<ISymbol> members)
        {
            if (property.SetMethod is { IsInitOnly: false })
            {
                return true;
            }

            // FieldIdAssignmentHelper uses a unique backing field for read-only and init-only properties,
            // including conventional hand-written backing fields and fields read from referenced implementations.
            var fields = members.OfType<IFieldSymbol>().Where(field => !field.IsStatic
                && SymbolEqualityComparer.Default.Equals(field.Type, property.Type)
                && (field.Name == $"<{property.Name}>k__BackingField"
                    || CanonicalName(field.Name) == CanonicalName(property.Name))).ToArray();
            if (fields.Length == 1)
            {
                return GetAttribute(fields[0], _nonSerialized) is null;
            }

            // Public-only metadata imports hide private backing fields. Include a known auto-property
            // conservatively; a producer's hidden field-level exclusion needs an explicit consumer suppression.
            return fields.Length == 0 && property.DeclaringSyntaxReferences.IsEmpty
                && property.GetMethod is { } getter && GetAttribute(getter, _compilerGenerated) is not null;
        }

        private static string CanonicalName(string name)
        {
            name = name.TrimStart('_');
            return name.Length > 0 && char.IsUpper(name[0]) ? char.ToLowerInvariant(name[0]) + name.Substring(1) : name;
        }
    }
}
