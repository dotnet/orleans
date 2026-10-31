using System.Text;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class SerializerFactoryGenerator
{
    // Configuration statements are written inside the context's partial class and configuration method.
    private const int ContextStatementIndentation = 2;

    internal sealed class Registration(ITypeSymbol type)
    {
        public ITypeSymbol Type { get; } = type;
        public string Codec { get; set; } = "";
        public string Copier { get; set; } = "";
        public string CodecConstruction { get; set; } = "";
        public string CopierConstruction { get; set; } = "";
        public bool HasBaseCodec { get; set; }
        public bool HasBaseCopier { get; set; }
        public List<ITypeSymbol> Dependencies { get; } = [];
        public ISerializableTypeDescription? Model { get; set; }
        public INamedTypeSymbol? ReferencedCodec { get; set; }
        public INamedTypeSymbol? ReferencedCopier { get; set; }
        public List<IArrayTypeSymbol> CanonicalArrays { get; } = [];
    }

    internal sealed class Graph(IReadOnlyDictionary<ITypeSymbol, Registration> registrations, string configurationStatements)
    {
        public IReadOnlyDictionary<ITypeSymbol, Registration> Registrations { get; } = registrations;
        public string ConfigurationStatements { get; } = configurationStatements;
    }

    internal sealed record Failure(ITypeSymbol Type, string Reason, bool RequiresImplementation = false);

    internal static bool HasSupportedModelConstruction(ISerializableTypeDescription model)
        => !model.HasComplexBaseType && !model.UseActivator && model.SerializationHooks.Count == 0;

    internal static bool TryCreate(
        IGeneratorServices services,
        IEnumerable<ITypeSymbol> roots,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out Graph? graph,
        [NotNullWhen(false)] out Failure? failure)
    {
        graph = null;
        failure = null;
        var implementationCompilation = services.Compilation is CSharpCompilation csharp
            ? csharp.WithOptions(csharp.Options.WithMetadataImportOptions(MetadataImportOptions.All))
            : services.Compilation;
        var registrations = new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default);
        var dependencyContracts = new SerializerDependencyContract.Index(services.Compilation);
        var pending = new Queue<ITypeSymbol>(roots);
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var type = pending.Dequeue().WithNullableAnnotation(NullableAnnotation.None);
            if (registrations.ContainsKey(type)) continue;
            if (registrations.Count >= 1024)
            {
                failure = new(type, "the dependency graph exceeds 1024 closed types; declare a finite serialization graph");
                return false;
            }
            var registration = new Registration(type);
            if (Describe(registration, services, implementationCompilation, dependencyContracts, cancellationToken, out var requiresImplementation) is { } reason)
            {
                if (!requiresImplementation)
                {
                    failure = new(type, reason);
                    return false;
                }

                // Keep checking contracts whose types are available even when runtime metadata is omitted.
                failure ??= new(type, reason, RequiresImplementation: true);
            }
            registrations.Add(type, registration);
            foreach (var dependency in registration.Dependencies) pending.Enqueue(dependency);
        }

        if (failure is not null)
        {
            var metadata = new SourceWriter(initialIndentation: ContextStatementIndentation);
            var visited = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
            foreach (var type in registrations.Keys)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (AppendTypeMetadata(metadata, type, services.LibraryTypes, visited) is { } metadataFailure)
                {
                    failure = metadataFailure;
                    break;
                }
            }

            return false;
        }

        var result = new SourceWriter(initialIndentation: ContextStatementIndentation);
        var metadataTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var auxiliaryServices = new HashSet<string>(StringComparer.Ordinal);

        foreach (var registration in registrations.Values.OrderBy(value => Name(value.Type), StringComparer.Ordinal))
        {
            var typeName = Name(registration.Type);
            var hasBaseCodec = registration.HasBaseCodec;
            var hasBaseCopier = registration.HasBaseCopier;
            if (registration.ReferencedCodec is { } referencedCodec)
            {
                var implementationLibrary = LibraryTypes.FromCompilation(implementationCompilation, services.Options);
                hasBaseCodec = referencedCodec.AllInterfaces.Any(contract =>
                    SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, implementationLibrary.BaseCodec_1));
                hasBaseCopier = registration.ReferencedCopier?.AllInterfaces.Any(contract =>
                    SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, implementationLibrary.BaseCopier_1)) == true;
                registration.CodecConstruction = ConstructReferenced(registration.Codec, referencedCodec);
                registration.CopierConstruction = registration.ReferencedCopier is { } referencedCopier
                    ? ConstructReferenced(registration.Copier, referencedCopier)
                    : $"new {registration.Copier}()";
            }
            else if (registration.Model is null && registration.Dependencies.Count > 0)
            {
                var codecArguments = new List<string>();
                var copierArguments = new List<string>();
                foreach (var dependency in registration.Dependencies)
                {
                    var target = registrations[dependency.WithNullableAnnotation(NullableAnnotation.None)];
                    var cyclic = Reaches(target, registration.Type, registrations, new(SymbolEqualityComparer.Default));
                    codecArguments.Add(cyclic
                        ? $"CreateCodecHolder<{Name(dependency)}>(provider)"
                        : $"provider.GetCodec<{Name(dependency)}>()");
                    copierArguments.Add(cyclic
                        ? $"CreateCopierHolder<{Name(dependency)}>(provider)"
                        : $"provider.GetDeepCopier<{Name(dependency)}>()");
                }

                registration.CodecConstruction = $"new {registration.Codec}({string.Join(", ", codecArguments)})";
                registration.CopierConstruction = $"new {registration.Copier}({string.Join(", ", copierArguments)})";
            }

            result.WriteLine($"options.AddSerializerService<{registration.Codec}>(static provider => {registration.CodecConstruction});");
            result.WriteLine($"options.AddSerializerService<{registration.Copier}>(static provider => {registration.CopierConstruction});");
            result.WriteLine($"options.AddSerializer<{typeName}>(static provider => {Resolve(registration.Codec)}, static provider => {Resolve(registration.Copier)});");
            if (registration.Model is { IsValueType: true, IsEnumType: false })
            {
                result.WriteLine($"options.AddSerializerService<global::Orleans.Serialization.Serializers.IValueSerializer<{typeName}>>(static provider => {Resolve(registration.Codec)});");
            }
            if (hasBaseCodec)
            {
                result.WriteLine($"options.AddSerializerService<global::Orleans.Serialization.Serializers.IBaseCodec<{typeName}>>(static provider => {Resolve(registration.Codec)});");
            }
            if (hasBaseCopier)
            {
                result.WriteLine($"options.AddSerializerService<global::Orleans.Serialization.Cloning.IBaseCopier<{typeName}>>(static provider => {Resolve(registration.Copier)});");
            }

            foreach (var array in registration.CanonicalArrays)
            {
                var arrayRegistration = registrations[array.WithNullableAnnotation(NullableAnnotation.None)];
                var element = registrations[array.ElementType.WithNullableAnnotation(NullableAnnotation.None)];
                var canonicalCodec = Name(services.LibraryTypes.ArrayCodec.Construct(array.ElementType));
                var canonicalCopier = Name(services.LibraryTypes.ArrayCopier.Construct(array.ElementType));
                var cyclic = Reaches(element, registration.Type, registrations, new(SymbolEqualityComparer.Default));
                if (arrayRegistration.Codec != canonicalCodec && auxiliaryServices.Add(canonicalCodec))
                {
                    var codecDependency = cyclic ? $"CreateCodecHolder<{Name(array.ElementType)}>(provider)" : $"provider.GetCodec<{Name(array.ElementType)}>()";
                    result.WriteLine($"options.AddSerializerService<{canonicalCodec}>(static provider => new {canonicalCodec}({codecDependency}));");
                }
                if (arrayRegistration.Copier != canonicalCopier && auxiliaryServices.Add(canonicalCopier))
                {
                    var copierDependency = cyclic ? $"CreateCopierHolder<{Name(array.ElementType)}>(provider)" : $"provider.GetDeepCopier<{Name(array.ElementType)}>()";
                    result.WriteLine($"options.AddSerializerService<{canonicalCopier}>(static provider => new {canonicalCopier}({copierDependency}));");
                }
            }

            result.WriteLine($"options.AddAllowedType(typeof({typeName}));");
            if (AppendTypeMetadata(result, registration.Type, services.LibraryTypes, metadataTypes) is { } metadataFailure)
            {
                failure = metadataFailure;
                return false;
            }
        }

        graph = new Graph(registrations, result.ToString());
        return true;
    }

    private static string? Describe(
        Registration registration, IGeneratorServices services, Compilation implementationCompilation,
        SerializerDependencyContract.Index dependencyContracts,
        CancellationToken cancellationToken, out bool requiresImplementation)
    {
        requiresImplementation = false;
        var type = registration.Type;
        if (type is INamedTypeSymbol { IsUnboundGenericType: true } || ContainsTypeParameter(type))
            return "use a closed type with explicit generic arguments";
        if (services.LibraryTypes.StaticCodecs.FindByUnderlyingType(type) is { } primitive
            && type.SpecialType != SpecialType.System_Object)
        {
            if (primitive.CodecType.InstanceConstructors.All(constructor => constructor.Parameters.Length > 0))
                return "this built-in type requires runtime service configuration";
            var copier = services.LibraryTypes.StaticCopiers.FindByUnderlyingType(type);
            if (copier is null && !services.LibraryTypes.IsShallowCopyable(type))
                return "this built-in type requires a statically registered deep copier";
            registration.Codec = Name(primitive.CodecType);
            registration.Copier = copier is { }
                ? Name(copier.CopierType)
                : $"global::Orleans.Serialization.Cloning.ShallowCopier<{Name(type)}>";
            registration.CodecConstruction = $"new {registration.Codec}()";
            registration.CopierConstruction = $"new {registration.Copier}()";
            return null;
        }

        if (type is IArrayTypeSymbol array)
        {
            if (!array.IsSZArray) return "use a single-dimensional zero-based array";
            registration.Codec = $"global::Orleans.Serialization.Codecs.ArrayCodec<{Name(array.ElementType)}>";
            registration.Copier = $"global::Orleans.Serialization.Codecs.ArrayCopier<{Name(array.ElementType)}>";
            registration.Dependencies.Add(array.ElementType);
            return null;
        }

        if (type is not INamedTypeSymbol named || named.IsRefLikeType)
            return "use a serializable class, struct, enum, or supported collection";
        if (named.ContainingType is { IsGenericType: true })
            return "use a model declared outside a generic containing type";
        var definition = named.OriginalDefinition.ToDisplayString();
        var collection = definition switch
        {
            "System.Collections.Generic.List<T>" => "List",
            "System.Collections.Generic.Dictionary<TKey, TValue>" => "Dictionary",
            "System.Nullable<T>" => "Nullable",
            _ => null
        };
        if (collection is not null)
        {
            var arguments = string.Join(", ", named.TypeArguments.Select(Name));
            registration.Codec = $"global::Orleans.Serialization.Codecs.{collection}Codec<{arguments}>";
            registration.Copier = $"global::Orleans.Serialization.Codecs.{collection}Copier<{arguments}>";
            registration.Dependencies.AddRange(named.TypeArguments);
            return null;
        }

        if (!named.HasAttribute(services.LibraryTypes.GenerateSerializerAttribute))
            return "apply GenerateSerializerAttribute to the model, or use List<T>, Dictionary<TKey, TValue>, Nullable<T>, or T[]";
        if (named.IsAbstract || named.TypeKind == TypeKind.Interface)
            return "declare concrete model types";
        if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, services.Compilation.Assembly))
            return DescribeReferenced(registration, named, services, implementationCompilation, dependencyContracts, cancellationToken, out requiresImplementation);
        ISerializableTypeDescription? model;
        try
        {
            model = SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, named);
        }
        catch (OrleansGeneratorDiagnosticAnalysisException exception)
        {
            return exception.Diagnostic.GetMessage();
        }
        if (model is null) return "the model has no generated serialization implementation";
        if (!HasSupportedModelConstruction(model))
            return "this context supports models with default construction, an object base, and generated member serialization";
        foreach (var member in model.Members)
        {
            if (!member.IsSerializable && !member.IsCopyable) continue;
            if (SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, services.Compilation.Assembly))
            {
                var generatedMember = new SerializerGenerator.SerializableMember(services, member);
                if (generatedMember.GetGetterFieldDescription() is { Initialization: not null }
                    || generatedMember.GetSetterFieldDescription() is { Initialization: not null })
                    return $"serialized member '{member.Symbol.Name}' requires statically generated field accessor support";
            }
            registration.Dependencies.Add(member.Type);
        }

        var argumentsSuffix = named.IsGenericType
            ? $"<{string.Join(", ", named.TypeArguments.Select(Name))}>"
            : "";
        var generatedNamespace = SerializerGenerator.GetGeneratedNamespaceName(named);
        registration.Codec = $"global::{generatedNamespace}.{SerializerGenerator.GetSimpleClassName(named.Name)}{argumentsSuffix}";
        var definitionModel = named.IsGenericType
            ? SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, named.OriginalDefinition)!
            : model;
        registration.Copier = definitionModel.IsShallowCopyable
            ? $"global::Orleans.Serialization.Cloning.ShallowCopier<{Name(type)}>"
            : $"global::{generatedNamespace}.{CopierGenerator.GetSimpleClassName(named.Name)}{argumentsSuffix}";
        registration.Model = definitionModel;
        var codecConstruction = new SerializerGenerator(services).GetConstruction(definitionModel);
        var copierConstruction = new CopierGenerator(services).GetConstruction(definitionModel);
        registration.CodecConstruction = ConstructGenerated(registration.Codec, codecConstruction);
        registration.CopierConstruction = ConstructGenerated(registration.Copier, copierConstruction);
        registration.HasBaseCodec = codecConstruction.HasBaseContract;
        registration.HasBaseCopier = copierConstruction.HasBaseContract;
        var substitutions = new Dictionary<ITypeParameterSymbol, ITypeSymbol>(SymbolEqualityComparer.Default);
        foreach (var (parameter, argument) in definitionModel.TypeParameters.Zip(named.GetAllTypeArguments(),
            static (parameter, argument) => (parameter.Parameter, argument)))
            substitutions.Add(parameter, argument);
        foreach (var request in codecConstruction.CanonicalArrays.Concat(copierConstruction.CanonicalArrays))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var canonicalArray = (IArrayTypeSymbol)CloseType(request);
            if (!registration.CanonicalArrays.Any(existing => SymbolEqualityComparer.Default.Equals(existing, canonicalArray)))
                registration.CanonicalArrays.Add(canonicalArray);
            registration.Dependencies.Add(canonicalArray);
            registration.Dependencies.Add(canonicalArray.ElementType);
        }

        return null;

        ITypeSymbol CloseType(ITypeSymbol symbol)
        {
            switch (symbol)
            {
                case ITypeParameterSymbol parameter:
                    return substitutions[parameter];
                case IArrayTypeSymbol array:
                    return services.Compilation.CreateArrayTypeSymbol(CloseType(array.ElementType), array.Rank);
                case INamedTypeSymbol { IsGenericType: true } generic:
                    var definition = generic is { TypeKind: not TypeKind.Error, ContainingType.IsGenericType: true }
                        ? ((INamedTypeSymbol)CloseType(generic.ContainingType)).GetTypeMembers(generic.Name, generic.Arity)
                            .Single(candidate => SymbolEqualityComparer.Default.Equals(candidate.OriginalDefinition, generic.OriginalDefinition))
                        : generic.ConstructedFrom;
                    return generic.Arity > 0
                        ? definition.Construct([.. generic.TypeArguments.Select(CloseType)])
                        : definition;
                default:
                    return symbol;
            }
        }
    }

    private static string? DescribeReferenced(
        Registration registration, INamedTypeSymbol named, IGeneratorServices services,
        Compilation implementationCompilation, SerializerDependencyContract.Index dependencyContracts,
        CancellationToken cancellationToken, out bool requiresImplementation)
    {
        requiresImplementation = false;
        var binding = implementationCompilation.GetSemanticModel(implementationCompilation.SyntaxTrees.First());
        var implementationType = binding.GetSpeculativeTypeInfo(0, named.ToTypeSyntax(),
            SpeculativeBindingOption.BindAsTypeOrNamespace).Type as INamedTypeSymbol;
        if (implementationType is null)
        {
            requiresImplementation = true;
            return "provide the referenced model's implementation metadata";
        }
        var library = LibraryTypes.FromCompilation(implementationCompilation, services.Options);
        var model = new SerializableTypeDescription(implementationCompilation, implementationType,
            SerializableSourceOutputGenerator.ShouldIncludePrimaryConstructorParameters(implementationType, library), [], library);
        if (!HasSupportedModelConstruction(model))
            return "this context supports models with default construction, an object base, and generated member serialization";
        registration.Model = model;
        registration.ReferencedCodec = ResolveImplementation(SerializerGenerator.GetSimpleClassName(named.Name));
        registration.ReferencedCopier = ResolveImplementation(CopierGenerator.GetSimpleClassName(named.Name));
        var consumerBinding = services.Compilation.GetSemanticModel(services.Compilation.SyntaxTrees.First());
        if (registration.ReferencedCodec is null || registration.ReferencedCopier is null && !model.IsShallowCopyable)
        {
            requiresImplementation = true;
            var constructorParameters = SerializableSourceOutputGenerator.ResolveConstructorParameters(
                implementationType, model.IncludePrimaryConstructorParameters, library);
            var members = new FieldIdAssignmentHelper(
                implementationType,
                constructorParameters,
                SerializableSourceOutputGenerator.GetGenerateFieldIdsOptionFromType(implementationType, library),
                library);
            foreach (var member in SerializableSourceOutputGenerator.GetDataMembers(members))
            {
                if (!member.IsSerializable && !member.IsCopyable) continue;
                cancellationToken.ThrowIfCancellationRequested();
                var dependency = consumerBinding.GetSpeculativeTypeInfo(0, member.Type.ToTypeSyntax(),
                    SpeculativeBindingOption.BindAsTypeOrNamespace).Type;
                if (dependency is null)
                    return "provide complete referenced model metadata to discover the producer's serialization dependencies";
                registration.Dependencies.Add(dependency);
            }
            return "provide the referenced assembly's generated codec and copier implementations";
        }
        registration.Codec = Name(registration.ReferencedCodec);
        registration.Copier = registration.ReferencedCopier is { } referencedCopier
            ? Name(referencedCopier)
            : Name(services.LibraryTypes.ShallowCopier.Construct(named));

        foreach (var implementation in new[] { registration.ReferencedCodec, registration.ReferencedCopier })
        {
            if (implementation is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
            if (dependencyContracts.TryRead(implementation, named, registration.Dependencies, registration.CanonicalArrays, out var contractFailure))
            {
                if (contractFailure is not null)
                {
                    return contractFailure;
                }

                continue;
            }

            if (SerializableSourceOutputGenerator.HasReferenceAssemblyAttribute(implementation.ContainingAssembly))
            {
                var options = new CodeGeneratorOptions();
                try
                {
                    var declared = SerializableSourceOutputGenerator.CreateSerializableTypeDescription(
                        services.Compilation, services.LibraryTypes, options, named);
                    if (declared is not null)
                        registration.Dependencies.AddRange(declared.Members.Where(static member => member.IsSerializable || member.IsCopyable)
                            .Select(static member => member.Type));
                }
                catch (OrleansGeneratorDiagnosticAnalysisException)
                {
                    requiresImplementation = true;
                    return "provide complete generated implementation metadata to discover the producer's serialization dependencies";
                }
            }

            foreach (var member in implementation.GetMembers())
            {
                IEnumerable<ITypeSymbol> serviceTypes = member switch
                {
                    IFieldSymbol field => new[] { field.Type },
                    IPropertySymbol property => new[] { property.Type },
                    IMethodSymbol method => method.Parameters.Select(static parameter => parameter.Type).Prepend(method.ReturnType),
                    _ => []
                };
                foreach (var serviceType in serviceTypes.OfType<INamedTypeSymbol>())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var service = consumerBinding.GetSpeculativeTypeInfo(0, serviceType.ToTypeSyntax(),
                        SpeculativeBindingOption.BindAsTypeOrNamespace).Type as INamedTypeSymbol;
                    if (service is null) continue;
                    foreach (var contract in service.AllInterfaces.Prepend(service))
                    {
                        if (SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, services.LibraryTypes.FieldCodec_1)
                            || SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, services.LibraryTypes.DeepCopier_1))
                        {
                            registration.Dependencies.Add(contract.TypeArguments[0]);
                        }
                    }

                    if (SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, services.LibraryTypes.ArrayCodec)
                        || SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, services.LibraryTypes.ArrayCopier))
                    {
                        var array = services.Compilation.CreateArrayTypeSymbol(service.TypeArguments[0]);
                        if (!registration.CanonicalArrays.Any(existing => SymbolEqualityComparer.Default.Equals(existing, array)))
                            registration.CanonicalArrays.Add(array);
                        registration.Dependencies.Add(array);
                        registration.Dependencies.Add(array.ElementType);
                    }
                }
            }
        }

        return null;

        INamedTypeSymbol? ResolveImplementation(string name)
        {
            var metadataName = $"{SerializerGenerator.GetGeneratedNamespaceName(named)}.{name}"
                + (named.TypeArguments.Length > 0 ? $"`{named.TypeArguments.Length}" : "");
            var implementation = implementationCompilation.GetTypeByMetadataName(metadataName);
            return implementation is { IsGenericType: true } ? implementation.Construct([.. implementationType.TypeArguments]) : implementation;
        }
    }

    private static string ConstructReferenced(string name, INamedTypeSymbol implementation)
    {
        var constructor = implementation.InstanceConstructors.Single(ctor => ctor.DeclaredAccessibility == Accessibility.Public);
        var arguments = constructor.Parameters.Select(parameter =>
            parameter.Type.ToDisplayString() == "Orleans.Serialization.Serializers.ICodecProvider"
                ? "provider"
                : Resolve(Name(parameter.Type)));
        return $"new {name}({string.Join(", ", arguments)})";
    }

    private static string ConstructGenerated(string name, GeneratedTypeConstruction construction)
    {
        var arguments = construction.Parameters.Select(parameter =>
            parameter.IsCodecProvider
                ? "provider"
                : Resolve(CodeText.Type(parameter.Type)));
        return $"new {name}({string.Join(", ", arguments)})";
    }

    private static Failure? AppendTypeMetadata(SourceWriter result, ITypeSymbol symbol, LibraryTypes library, HashSet<ITypeSymbol> visited, int depth = 0)
    {
        if (visited.Contains(symbol)) return null;
        if (visited.Count >= 1024)
            return new Failure(symbol, "the metadata dependency graph exceeds 1024 closed types; declare finite type metadata");
        if (depth >= 128)
            return new Failure(symbol, "the metadata dependency graph exceeds 128 nested dependencies; declare finite type metadata");
        visited.Add(symbol);
        if (symbol is IArrayTypeSymbol array)
        {
            return AppendTypeMetadata(result, array.ElementType, library, visited, depth + 1);
        }
        if (symbol is not INamedTypeSymbol type) return null;
        if (!library.Compilation.IsSymbolAccessibleWithin(type.OriginalDefinition, library.Compilation.Assembly))
        {
            return type.HasAttribute(library.AliasAttribute)
                || type.HasAttribute(library.CompoundTypeAliasAttribute)
                || GeneratedCodeUtilities.GetId(library, type) is not null
                ? new Failure(type, "required alias or type-identifier metadata is inaccessible to the generated context")
                : null;
        }
        if (type.ContainingType is { } declaring)
        {
            if (AppendTypeMetadata(result, declaring, library, visited, depth + 1) is { } declaringFailure) return declaringFailure;
        }
        foreach (var contract in type.AllInterfaces)
        {
            if (AppendTypeMetadata(result, contract, library, visited, depth + 1) is { } contractFailure) return contractFailure;
        }
        foreach (var argument in type.TypeArguments)
        {
            if (!ContainsTypeParameter(argument))
            {
                if (library.Compilation.IsSymbolAccessibleWithin(argument, library.Compilation.Assembly))
                    result.WriteLine($"options.AddAllowedType(typeof({Name(argument)}));");
                if (AppendTypeMetadata(result, argument, library, visited, depth + 1) is { } argumentFailure) return argumentFailure;
            }
        }
        // Only metadata-bearing types need their open spelling; predefined types have no generic definition to spell.
        string? openTypeText = null;
        string OpenType() => openTypeText ??= CodeText.Type(type.ToOpenTypeSyntax());
        if (type.HasAttribute(library.AliasAttribute) || type.HasAttribute(library.CompoundTypeAliasAttribute)
            || GeneratedCodeUtilities.GetId(library, type) is not null)
        {
            result.WriteLine($"options.AddAllowedType(typeof({OpenType()}));");
        }
        if (GeneratedCodeUtilities.GetAlias(library, type) is { } alias)
        {
            MetadataGenerator.WriteTypeMetadataRegistration(result, "options.WellKnownTypeAliases", CodeText.QuotedText(alias), $"typeof({OpenType()})");
        }

        if (GeneratedCodeUtilities.GetId(library, type) is { } id)
        {
            MetadataGenerator.WriteTypeMetadataRegistration(result, "options.WellKnownTypeIds", CodeText.Literal(id), $"typeof({OpenType()})");
        }

        foreach (var compound in type.GetAttributes().Where(attribute =>
            SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, library.CompoundTypeAliasAttribute)
            && attribute.ConstructorArguments.Length == 1))
        {
            var components = compound.ConstructorArguments[0].Values;
            foreach (var componentType in components.Select(component => component.Value).OfType<ITypeSymbol>())
            {
                if (!library.Compilation.IsSymbolAccessibleWithin(componentType, library.Compilation.Assembly))
                    return new Failure(componentType, "required compound-alias component metadata is inaccessible to the generated context");
                result.WriteLine($"options.AddAllowedType(typeof({Name(componentType)}));");
                if (AppendTypeMetadata(result, componentType, library, visited, depth + 1) is { } componentFailure) return componentFailure;
            }

            var compoundAlias = new StringBuilder("options.CompoundTypeAliases");
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index].Value switch
                {
                    string value => CodeText.QuotedText(value),
                    ITypeSymbol componentSymbol => $"typeof({Name(componentSymbol)})",
                    _ => throw new InvalidOperationException("Compound aliases contain type or string components.")
                };
                compoundAlias.Append(index == components.Length - 1 ? ".Add(" : ".GetOrAdd(").Append(component);
                if (index == components.Length - 1)
                    compoundAlias.Append(", typeof(").Append(OpenType()).Append(')');
                compoundAlias.Append(')');
            }

            result.WriteLine(compoundAlias.Append(';').ToString());
        }
        return null;
    }

    private static bool Reaches(Registration current, ITypeSymbol target,
        Dictionary<ITypeSymbol, Registration> registrations, HashSet<ITypeSymbol> visited)
    {
        if (SymbolEqualityComparer.Default.Equals(current.Type, target)) return true;
        if (!visited.Add(current.Type)) return false;
        return current.Dependencies.Any(dependency =>
            Reaches(registrations[dependency.WithNullableAnnotation(NullableAnnotation.None)], target, registrations, visited));
    }

    private static bool ContainsTypeParameter(ITypeSymbol type)
        => type is ITypeParameterSymbol
            || type is INamedTypeSymbol named && named.TypeArguments.Any(ContainsTypeParameter)
            || type is IArrayTypeSymbol array && ContainsTypeParameter(array.ElementType);

    private static string Resolve(string type)
        => $"global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<{type}>(null!, provider)";

    private static string Name(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

}
