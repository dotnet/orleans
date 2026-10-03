using System.Text;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class SerializerFactoryGenerator
{
    internal sealed class Registration(ITypeSymbol type)
    {
        public ITypeSymbol Type { get; } = type;
        public string Codec { get; set; } = "";
        public string Copier { get; set; } = "";
        public string CodecConstruction { get; set; } = "";
        public string CopierConstruction { get; set; } = "";
        public List<ITypeSymbol> Dependencies { get; } = [];
        public ISerializableTypeDescription? Model { get; set; }
        public ITypeSymbol? ResponseResult { get; set; }
        public INamedTypeSymbol? ReferencedCodec { get; set; }
        public INamedTypeSymbol? ReferencedCopier { get; set; }
        public List<IArrayTypeSymbol> CanonicalArrays { get; } = [];
    }

    internal sealed class Graph(IReadOnlyDictionary<ITypeSymbol, Registration> registrations, string configurationStatements)
    {
        public IReadOnlyDictionary<ITypeSymbol, Registration> Registrations { get; } = registrations;
        public string ConfigurationStatements { get; } = configurationStatements;
    }

    internal sealed record Failure(ITypeSymbol Type, string Reason);

    internal static Graph? CreateRpcModelRoot(
        IGeneratorServices services,
        INamedTypeSymbol type,
        CancellationToken cancellationToken)
        => CreateRpcModelRoot(services, type, cancellationToken, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), includeResponse: true);

    private static Graph? CreateRpcModelRoot(
        IGeneratorServices services,
        INamedTypeSymbol type,
        CancellationToken cancellationToken,
        HashSet<ITypeSymbol> constructionTypes,
        bool includeResponse)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var originalBinding = services.Compilation.GetSemanticModel(services.Compilation.SyntaxTrees.First());
        type = originalBinding.GetSpeculativeTypeInfo(0, type.ToTypeSyntax(), SpeculativeBindingOption.BindAsTypeOrNamespace).Type as INamedTypeSymbol
            ?? throw new InvalidOperationException($"Unable to bind serialization construction type {type}.");
        if (!constructionTypes.Add(type) || ContainsTypeParameter(type) || type.ContainingType is { IsGenericType: true }
            || type.IsAbstract || type.TypeKind == TypeKind.Interface
            || !type.HasAttribute(services.LibraryTypes.GenerateSerializerAttribute))
        {
            return null;
        }

        var registration = new Registration(type);
        var inspectionCompilation = services.Compilation;
        var inspectionType = type;
        var inspectionLibrary = services.LibraryTypes;
        if (!SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, services.Compilation.Assembly))
        {
            inspectionCompilation = services.Compilation.WithOptions(services.Compilation.Options.WithMetadataImportOptions(MetadataImportOptions.All));
            var binding = inspectionCompilation.GetSemanticModel(inspectionCompilation.SyntaxTrees.First());
            inspectionType = binding.GetSpeculativeTypeInfo(0, type.ToTypeSyntax(), SpeculativeBindingOption.BindAsTypeOrNamespace).Type as INamedTypeSymbol
                ?? throw new InvalidOperationException($"Unable to bind referenced serialization type {type}.");
            inspectionLibrary = LibraryTypes.FromCompilation(inspectionCompilation, services.Options);
        }
        ISerializableTypeDescription? constructionModel;
        try
        {
            if (SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, services.Compilation.Assembly))
            {
                constructionModel = SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, type);
            }
            else
            {
                // Reference assemblies preserve constructor contracts, but can omit serialized private fields.
                var fields = new FieldIdAssignmentHelper(inspectionType, [], GenerateFieldIds.None, inspectionLibrary);
                constructionModel = new SerializableTypeDescription(inspectionCompilation, inspectionType, false,
                    SerializableSourceOutputGenerator.GetDataMembers(fields), inspectionLibrary);
            }
        }
        catch (OrleansGeneratorDiagnosticAnalysisException)
        {
            return null;
        }
        if (constructionModel is null)
        {
            return null;
        }

        var requiresActivator = false;
        INamedTypeSymbol? referencedActivator = null;
        var codecDependencies = new List<string>();
        var copierDependencies = new List<string>();
        if (SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, services.Compilation.Assembly))
        {
            var model = constructionModel;
            DescribeGeneratedModel(registration, type, model);
            var codecDeclaration = new SerializerGenerator(services).Generate(model);
            var copierDeclaration = new CopierGenerator(services).GenerateCopier(model, new());
            registration.CodecConstruction = ConstructGenerated(registration.Codec, codecDeclaration);
            registration.CopierConstruction = copierDeclaration is null
                ? $"new {registration.Copier}()"
                : ConstructGenerated(registration.Copier, copierDeclaration);
            requiresActivator = model.UseActivator;
            codecDependencies.AddRange(ConstructorDependencies(codecDeclaration));
            if (copierDeclaration is not null)
                copierDependencies.AddRange(ConstructorDependencies(copierDeclaration));
        }
        else
        {
            var generatedNamespace = SerializerGenerator.GetGeneratedNamespaceName(type);
            var codecType = ResolveModelImplementation(SerializerGenerator.GetSimpleClassName(type.Name));
            if (codecType is null)
            {
                return null;
            }

            registration.Codec = Name(codecType);
            registration.CodecConstruction = ConstructReferenced(registration.Codec, codecType);
            codecDependencies.AddRange(ConstructorDependencies(codecType));
            requiresActivator = HasActivatorDependency(codecType);
            if (services.LibraryTypes.IsShallowCopyable(type))
            {
                registration.Copier = $"global::Orleans.Serialization.Cloning.ShallowCopier<{Name(type)}>";
                registration.CopierConstruction = $"new {registration.Copier}()";
            }
            else
            {
                var copierType = ResolveModelImplementation(CopierGenerator.GetSimpleClassName(type.Name));
                if (copierType is null)
                {
                    return null;
                }

                registration.Copier = Name(copierType);
                registration.CopierConstruction = ConstructReferenced(registration.Copier, copierType);
                copierDependencies.AddRange(ConstructorDependencies(copierType));
                requiresActivator |= HasActivatorDependency(copierType);
            }

            INamedTypeSymbol? ResolveModelImplementation(string name)
            {
                var arity = type.TypeArguments.Length;
                var definition = inspectionCompilation.GetTypeByMetadataName($"{generatedNamespace}.{name}{(arity > 0 ? $"`{arity}" : "")}");
                return definition is null || arity == 0 ? definition : definition.Construct([.. type.TypeArguments]);
            }
            if (requiresActivator)
                referencedActivator = ResolveModelImplementation(ActivatorGenerator.GetSimpleClassName(constructionModel));
        }

        if (requiresActivator
            && (type.HasAttribute(services.LibraryTypes.UseActivatorAttribute) && !constructionModel.HasActivatorConstructor && referencedActivator is null
                || constructionModel.HasActivatorConstructor && !SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, services.Compilation.Assembly)
                    && (referencedActivator is null || !inspectionCompilation.IsSymbolAccessibleWithin(referencedActivator, inspectionCompilation.Assembly))))
        {
            return null;
        }

        var responseType = services.Compilation.GetTypeByMetadataName("Orleans.Serialization.Invocation.Response`1")!.Construct(type);
        var codec = $"global::Orleans.Serialization.Invocation.PooledResponseCodec<{Name(type)}, {registration.Codec}>";
        var copier = $"global::Orleans.Serialization.Invocation.PooledResponseCopier<{Name(type)}, {registration.Copier}>";
        var result = new StringBuilder();
        foreach (var member in constructionModel.Members)
        {
            var memberName = Name(member.Type);
            if (member.IsSerializable)
                codecDependencies.Add($"global::Orleans.Serialization.Codecs.IFieldCodec<{memberName}>");
            if (member.IsCopyable && !services.LibraryTypes.IsShallowCopyable(type))
                copierDependencies.Add($"global::Orleans.Serialization.Cloning.IDeepCopier<{memberName}>");
        }
        foreach (var memberType in constructionModel.Members
            .Where(static member => member.IsSerializable || member.IsCopyable)
            .Select(static member => member.Type)
            .Distinct<ITypeSymbol>(SymbolEqualityComparer.Default))
        {
            var originalType = originalBinding.GetSpeculativeTypeInfo(0, memberType.ToTypeSyntax(), SpeculativeBindingOption.BindAsTypeOrNamespace).Type
                ?? throw new InvalidOperationException($"Unable to bind serialization construction dependency {memberType}.");
            AppendConstructionDependency(services, originalType, cancellationToken, constructionTypes, result);
        }

        if (referencedActivator is not null)
        {
            result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Activators.IActivator<")
                .Append(Name(type)).Append(">, ").Append(Name(referencedActivator)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(referencedActivator), referencedActivator))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(referencedActivator))).AppendLine(");");
        }
        else if (requiresActivator && constructionModel.HasActivatorConstructor
            && SymbolEqualityComparer.Default.Equals(type.ContainingAssembly, services.Compilation.Assembly))
        {
            var activatorName = $"global::{constructionModel.GeneratedNamespace}.{ActivatorGenerator.GetSimpleClassName(constructionModel)}";
            var activator = new ActivatorGenerator(services).GenerateActivator(constructionModel);
            result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Activators.IActivator<")
                .Append(Name(type)).Append(">, ").Append(activatorName).Append(">(static provider => ")
                .Append(ConstructGenerated(activatorName, activator))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(activator))).AppendLine(");");
        }
        else if (requiresActivator && !type.HasAttribute(services.LibraryTypes.UseActivatorAttribute)
            && !constructionModel.HasActivatorConstructor)
        {
            var factory = type.IsValueType ? "CreateDefaultValueTypeActivator" : "CreateDefaultReferenceTypeActivator";
            result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Activators.IActivator<")
                .Append(Name(type)).Append(">, global::Orleans.Serialization.Activators.IActivator<").Append(Name(type))
                .Append(">>(static provider => global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.")
                .Append(factory).Append('<').Append(Name(type)).AppendLine(">());");
        }

        result.Append("options.AddDefaultSerializerService<").Append(registration.Codec).Append(">(static provider => ")
            .Append(registration.CodecConstruction)
            .Append(", dependencies: ").Append(DefaultServiceDependencies(codecDependencies)).AppendLine(");");
        result.Append("options.AddDefaultSerializerService<").Append(registration.Copier).Append(">(static provider => ")
            .Append(registration.CopierConstruction)
            .Append(", dependencies: ").Append(DefaultServiceDependencies(copierDependencies)).AppendLine(");");
        result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.IFieldCodec<")
            .Append(Name(type)).Append(">, ").Append(registration.Codec).Append(">(static provider => ")
            .Append(Resolve(registration.Codec))
            .Append(", dependencies: ").Append(DefaultServiceDependencies(codecDependencies)).AppendLine(");");
        result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.IDeepCopier<")
            .Append(Name(type)).Append(">, ").Append(registration.Copier).Append(">(static provider => ")
            .Append(Resolve(registration.Copier))
            .Append(", dependencies: ").Append(DefaultServiceDependencies(copierDependencies)).AppendLine(");");
        if (type.IsValueType && type.TypeKind != TypeKind.Enum)
        {
            result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Serializers.IValueSerializer<")
                .Append(Name(type)).Append(">, ").Append(registration.Codec).Append(">(static provider => ")
                .Append(Resolve(registration.Codec))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(codecDependencies)).AppendLine(");");
        }
        if (!includeResponse)
        {
            return new Graph(new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default) { [type] = registration }, result.ToString());
        }

        result.Append("options.AddDefaultSerializerService<").Append(codec).Append(">(static provider => new ")
            .Append(codec).Append("(caller => ").Append(Resolve(registration.Codec, "caller"))
            .Append("), dependencies: ").Append(DefaultDependencyServices([type], codec: true)).AppendLine(");");
        result.Append("options.AddDefaultSerializerService<").Append(copier).Append(">(static provider => new ")
            .Append(copier).Append("(caller => ").Append(Resolve(registration.Copier, "caller"))
            .Append("), dependencies: ").Append(DefaultDependencyServices([type], codec: false)).AppendLine(");");
        result.Append("options.AddDefaultSerializer<").Append(Name(responseType)).Append(", ").Append(codec).Append(", ").Append(copier).Append(">(static provider => ")
            .Append(Resolve(codec)).Append(", static provider => ").Append(Resolve(copier))
            .Append(", codecDependencies: ").Append(DefaultDependencyServices([type], codec: true))
            .Append(", copierDependencies: ").Append(DefaultDependencyServices([type], codec: false)).AppendLine(");");
        result.Append("options.AddAllowedType(typeof(").Append(Name(responseType)).AppendLine("));");
        return new Graph(new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default) { [type] = registration }, result.ToString());

        bool HasActivatorDependency(INamedTypeSymbol implementation)
            => implementation.InstanceConstructors.SelectMany(static constructor => constructor.Parameters)
                .Any(parameter => parameter.Type is INamedTypeSymbol dependency
                    && SymbolEqualityComparer.Default.Equals(dependency.OriginalDefinition, inspectionLibrary.IActivator_1)
                    && SymbolEqualityComparer.Default.Equals(dependency.TypeArguments[0], inspectionType));

    }

    internal static Graph CreateRpcConstructionRoot(IGeneratorServices services, ITypeSymbol type, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        AppendConstructionDependency(services, type, cancellationToken, new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default), result);
        return new Graph(new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default), result.ToString());
    }

    private static void AppendConstructionDependency(IGeneratorServices services, ITypeSymbol dependency, CancellationToken cancellationToken,
        HashSet<ITypeSymbol> constructionTypes, StringBuilder result)
    {
        cancellationToken.ThrowIfCancellationRequested();
        dependency = dependency.WithNullableAnnotation(NullableAnnotation.None);
        if (constructionTypes.Contains(dependency)) return;
        if (TryCreate(services, [dependency], cancellationToken, out var finite, out _, useDefaultFactories: true)
            && !finite.Registrations.Keys.OfType<INamedTypeSymbol>().Any(named =>
                SymbolEqualityComparer.Default.Equals(named.OriginalDefinition, services.Compilation.GetTypeByMetadataName("System.Collections.Generic.Dictionary`2"))))
        {
            foreach (var registered in finite.Registrations.Keys) constructionTypes.Add(registered);
            result.AppendLine(finite.ConfigurationStatements);
            return;
        }

        if (dependency is INamedTypeSymbol modelType && modelType.HasAttribute(services.LibraryTypes.GenerateSerializerAttribute)
            && CreateRpcModelRoot(services, modelType, cancellationToken, constructionTypes, includeResponse: false) is { } modelGraph)
        {
            result.AppendLine(modelGraph.ConfigurationStatements);
            return;
        }
        else
        {
            constructionTypes.Add(dependency);
        }

        if (dependency is INamedTypeSymbol tupleType && TryGetTupleServices(services, tupleType, out var tupleCodec, out var tupleCopier))
        {
            foreach (var element in tupleType.TypeArguments) AppendConstructionDependency(services, element, cancellationToken, constructionTypes, result);
            result.Append("options.AddDefaultSerializerService<").Append(Name(tupleCodec)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(tupleCodec), tupleCodec))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(tupleCodec))).AppendLine(");");
            result.Append("options.AddDefaultSerializerService<").Append(Name(tupleCopier)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(tupleCopier), tupleCopier))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(tupleCopier))).AppendLine(");");
            result.Append("options.AddDefaultSerializer<").Append(Name(tupleType)).Append(", ").Append(Name(tupleCodec)).Append(", ").Append(Name(tupleCopier)).Append(">(static provider => ")
                .Append(Resolve(Name(tupleCodec))).Append(", static provider => ").Append(Resolve(Name(tupleCopier)))
                .Append(", codecDependencies: ").Append(DefaultDependencyServices(tupleType.TypeArguments, codec: true))
                .Append(", copierDependencies: ").Append(DefaultDependencyServices(tupleType.TypeArguments, codec: false)).AppendLine(");");
            return;
        }

        if (dependency is IArrayTypeSymbol { IsSZArray: true } arrayType)
        {
            AppendConstructionDependency(services, arrayType.ElementType, cancellationToken, constructionTypes, result);
            var codec = services.LibraryTypes.ArrayCodec.Construct(arrayType.ElementType);
            var copier = services.LibraryTypes.ArrayCopier.Construct(arrayType.ElementType);
            result.Append("options.AddDefaultSerializerService<").Append(Name(codec)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(codec), codec))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(codec))).AppendLine(");");
            result.Append("options.AddDefaultSerializerService<").Append(Name(copier)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(copier), copier))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(copier))).AppendLine(");");
            result.Append("options.AddDefaultSerializer<").Append(Name(arrayType)).Append(", ").Append(Name(codec)).Append(", ").Append(Name(copier)).Append(">(static provider => ")
                .Append(Resolve(Name(codec))).Append(", static provider => ").Append(Resolve(Name(copier)))
                .Append(", codecDependencies: ").Append(DefaultDependencyServices([arrayType.ElementType], codec: true))
                .Append(", copierDependencies: ").Append(DefaultDependencyServices([arrayType.ElementType], codec: false)).AppendLine(");");
            return;
        }

        if (dependency is INamedTypeSymbol collection
            && TryGetConstructionCollectionServices(services, collection, out var collectionCodec, out var collectionCopier))
        {
            var codec = collectionCodec;
            var copier = collectionCopier;
            foreach (var argument in collection.TypeArguments) AppendConstructionDependency(services, argument, cancellationToken, constructionTypes, result);
            foreach (var parameter in codec.InstanceConstructors.SelectMany(static constructor => constructor.Parameters))
            {
                if (parameter.Type is INamedTypeSymbol contract
                    && (SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, services.LibraryTypes.FieldCodec_1)
                        || SymbolEqualityComparer.Default.Equals(contract.OriginalDefinition, services.LibraryTypes.ValueSerializer)))
                    AppendConstructionDependency(services, contract.TypeArguments[0], cancellationToken, constructionTypes, result);
            }
            result.Append("options.AddDefaultSerializerService<").Append(Name(codec)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(codec), codec, preferCompleteConstructor: true))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(codec, preferCompleteConstructor: true))).AppendLine(");");
            result.Append("options.AddDefaultSerializerService<").Append(Name(copier)).Append(">(static provider => ")
                .Append(ConstructReferenced(Name(copier), copier))
                .Append(", dependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(copier))).AppendLine(");");
            result.Append("options.AddDefaultSerializer<").Append(Name(collection)).Append(", ").Append(Name(codec)).Append(", ").Append(Name(copier)).Append(">(static provider => ")
                .Append(Resolve(Name(codec))).Append(", static provider => ").Append(Resolve(Name(copier)))
                .Append(", codecDependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(codec, preferCompleteConstructor: true)))
                .Append(", copierDependencies: ").Append(DefaultServiceDependencies(ConstructorDependencies(copier))).AppendLine(");");
            return;
        }

        var name = Name(dependency);
        IEnumerable<ITypeSymbol> bridgeDependencies = dependency switch
        {
            INamedTypeSymbol bridge => bridge.TypeArguments.AsEnumerable(),
            IArrayTypeSymbol bridge => [bridge.ElementType],
            _ => []
        };
        // Metadata bridges add service contracts, while type dispatch stays with ordinary metadata.
        result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Codecs.IFieldCodec<")
            .Append(name).Append(">>(static provider => provider.GetCodec<").Append(name)
            .Append(">(), dependencies: ").Append(DefaultDependencyServices(bridgeDependencies, codec: true)).AppendLine(");");
        result.Append("options.AddDefaultSerializerService<global::Orleans.Serialization.Cloning.IDeepCopier<")
            .Append(name).Append(">>(static provider => provider.GetDeepCopier<").Append(name)
            .Append(">(), dependencies: ").Append(DefaultDependencyServices(bridgeDependencies, codec: false)).AppendLine(");");
        if (dependency is INamedTypeSymbol named)
        {
            foreach (var argument in named.TypeArguments) AppendConstructionDependency(services, argument, cancellationToken, constructionTypes, result);
            if (services.LibraryTypes.WellKnownCodecs.FindByUnderlyingType(named.OriginalDefinition) is { } knownCodec
                && knownCodec.CodecType.IsGenericType)
            {
                var codec = knownCodec.CodecType.Construct([.. named.TypeArguments]);
                foreach (var parameter in codec.InstanceConstructors.SelectMany(static constructor => constructor.Parameters))
                {
                    if (parameter.Type is INamedTypeSymbol service
                        && (SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, services.LibraryTypes.FieldCodec_1)
                            || SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, services.LibraryTypes.DeepCopier_1)))
                    {
                        AppendConstructionDependency(services, service.TypeArguments[0], cancellationToken, constructionTypes, result);
                    }
                }
            }
        }
        else if (dependency is IArrayTypeSymbol array)
        {
            AppendConstructionDependency(services, array.ElementType, cancellationToken, constructionTypes, result);
        }
    }

    internal static bool TryCreate(
        IGeneratorServices services,
        IEnumerable<ITypeSymbol> roots,
        CancellationToken cancellationToken,
        [NotNullWhen(true)] out Graph? graph,
        [NotNullWhen(false)] out Failure? failure,
        bool useDefaultFactories = false)
    {
        graph = null;
        failure = null;
        var registrations = new Dictionary<ITypeSymbol, Registration>(SymbolEqualityComparer.Default);
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
            if (Describe(registration, services, cancellationToken) is { } reason)
            {
                failure = new(type, reason);
                return false;
            }
            registrations.Add(type, registration);
            foreach (var dependency in registration.Dependencies) pending.Enqueue(dependency);
        }

        var result = new StringBuilder();
        foreach (var registration in registrations.Values)
        {
            ResolveResponseImplementations(registration);
        }

        var addService = useDefaultFactories ? "AddDefaultSerializerService" : "AddSerializerService";
        var addSerializer = useDefaultFactories ? "AddDefaultSerializer" : "AddSerializer";
        var metadataTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var auxiliaryServices = new HashSet<string>(StringComparer.Ordinal);

        foreach (var registration in registrations.Values.OrderBy(value => Name(value.Type), StringComparer.Ordinal))
        {
            var typeName = Name(registration.Type);
            if (registration.Model is not null)
            {
                ConstructGeneratedModel(registration, services);
            }
            else if (registration.Dependencies.Count > 0)
            {
                var codecArguments = new List<string>();
                var copierArguments = new List<string>();
                foreach (var dependency in registration.Dependencies)
                {
                    var target = registrations[dependency.WithNullableAnnotation(NullableAnnotation.None)];
                    var cyclic = Reaches(target, registration.Type, registrations, new(SymbolEqualityComparer.Default));
                    codecArguments.Add(cyclic && registration.ResponseResult is not null
                        ? $"caller => {Resolve(target.Codec, "caller")}"
                        : cyclic
                        ? $"CreateCodecHolder<{Name(dependency)}>(provider)"
                        : Resolve(target.Codec));
                    copierArguments.Add(cyclic && registration.ResponseResult is not null
                        ? $"caller => {Resolve(target.Copier, "caller")}"
                        : cyclic
                        ? $"CreateCopierHolder<{Name(dependency)}>(provider)"
                        : Resolve(target.Copier));
                }

                registration.CodecConstruction = $"new {registration.Codec}({string.Join(", ", codecArguments)})";
                registration.CopierConstruction = $"new {registration.Copier}({string.Join(", ", copierArguments)})";
            }

            result.Append("options.").Append(addService).Append('<').Append(registration.Codec).Append(">(static provider => ")
                .Append(registration.CodecConstruction);
            if (useDefaultFactories && registration.Dependencies.Count > 0)
                result.Append(", dependencies: ").Append(DefaultDependencyServices(registration.Dependencies, codec: true));
            result.AppendLine(");");
            result.Append("options.").Append(addService).Append('<').Append(registration.Copier).Append(">(static provider => ")
                .Append(registration.CopierConstruction);
            if (useDefaultFactories && registration.Dependencies.Count > 0)
                result.Append(", dependencies: ").Append(DefaultDependencyServices(registration.Dependencies, codec: false));
            result.AppendLine(");");
            result.Append("options.").Append(addSerializer).Append('<').Append(typeName);
            if (useDefaultFactories)
                result.Append(", ").Append(registration.Codec).Append(", ").Append(registration.Copier);
            result.Append(">(static provider => ")
                .Append(Resolve(registration.Codec)).Append(", static provider => ").Append(Resolve(registration.Copier));
            if (useDefaultFactories && registration.Dependencies.Count > 0)
            {
                result.Append(", codecDependencies: ").Append(DefaultDependencyServices(registration.Dependencies, codec: true))
                    .Append(", copierDependencies: ").Append(DefaultDependencyServices(registration.Dependencies, codec: false));
            }
            result.AppendLine(");");
            if (registration.Model is { IsValueType: true, IsEnumType: false })
            {
                result.Append("options.").Append(addService).Append("<global::Orleans.Serialization.Serializers.IValueSerializer<")
                    .Append(typeName).Append('>');
                if (useDefaultFactories) result.Append(", ").Append(registration.Codec);
                result.Append(">(static provider => ").Append(Resolve(registration.Codec)).AppendLine(");");
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
                    var codecDependency = cyclic ? $"CreateCodecHolder<{Name(array.ElementType)}>(provider)" : Resolve(element.Codec);
                    result.Append("options.").Append(addService).Append('<').Append(canonicalCodec).Append(">(static provider => new ")
                        .Append(canonicalCodec).Append('(').Append(codecDependency).AppendLine("));");
                }
                if (arrayRegistration.Copier != canonicalCopier && auxiliaryServices.Add(canonicalCopier))
                {
                    var copierDependency = cyclic ? $"CreateCopierHolder<{Name(array.ElementType)}>(provider)" : Resolve(element.Copier);
                    result.Append("options.").Append(addService).Append('<').Append(canonicalCopier).Append(">(static provider => new ")
                        .Append(canonicalCopier).Append('(').Append(copierDependency).AppendLine("));");
                }
            }

            result.Append("options.AddAllowedType(typeof(").Append(typeName).AppendLine("));");
            if (!useDefaultFactories && registration.Type is INamedTypeSymbol named)
            {
                AppendTypeMetadata(result, named, services.LibraryTypes, metadataTypes);
            }
        }

        graph = new Graph(registrations, result.ToString());
        return true;

        void ResolveResponseImplementations(Registration registration)
        {
            if (registration.ResponseResult is not { } resultType || registration.Codec.Length > 0)
            {
                return;
            }

            var target = registrations[resultType.WithNullableAnnotation(NullableAnnotation.None)];
            ResolveResponseImplementations(target);
            registration.Codec = $"global::Orleans.Serialization.Invocation.PooledResponseCodec<{Name(resultType)}, {target.Codec}>";
            registration.Copier = $"global::Orleans.Serialization.Invocation.PooledResponseCopier<{Name(resultType)}, {target.Copier}>";
        }
    }

    private static string DefaultDependencyServices(IEnumerable<ITypeSymbol> dependencies, bool codec)
        => DefaultServiceDependencies(dependencies.Select(dependency =>
            $"global::Orleans.Serialization.{(codec ? "Codecs.IFieldCodec" : "Cloning.IDeepCopier")}<{Name(dependency)}>"));

    private static string DefaultServiceDependencies(IEnumerable<string> dependencies)
        => "new global::System.Type[] { " + string.Join(", ", dependencies
            .Where(static type => type != "global::Orleans.Serialization.Serializers.ICodecProvider")
            .Distinct(StringComparer.Ordinal).Select(static type => $"typeof({type})")) + " }";

    private static IEnumerable<string> ConstructorDependencies(INamedTypeSymbol implementation, bool preferCompleteConstructor = false)
        => GetReferencedConstructor(implementation, preferCompleteConstructor).Parameters.Select(static parameter => Name(parameter.Type));

    private static IEnumerable<string> ConstructorDependencies(ClassDeclarationSyntax declaration)
        => (declaration.Members.OfType<ConstructorDeclarationSyntax>().SingleOrDefault()?.ParameterList.Parameters
                .Select(static parameter => parameter.Type!.ToString()) ?? [])
            .Concat(declaration.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(static invocation => invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "GetService" } })
                .Select(static invocation => ((GenericNameSyntax)((MemberAccessExpressionSyntax)invocation.Expression).Name)
                    .TypeArgumentList.Arguments.Single().ToString()));

    private static string? Describe(Registration registration, IGeneratorServices services, CancellationToken cancellationToken)
    {
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
        if (definition == "Orleans.Serialization.Invocation.Response<TResult>")
        {
            registration.ResponseResult = named.TypeArguments[0];
            registration.Dependencies.Add(named.TypeArguments[0]);
            return null;
        }

        if (TryGetTupleServices(services, named, out var tupleCodec, out var tupleCopier))
        {
            registration.Codec = Name(tupleCodec);
            registration.Copier = Name(tupleCopier);
            registration.Dependencies.AddRange(named.TypeArguments);
            return null;
        }

        var collection = definition switch
        {
            "System.Collections.Generic.List<T>" => "List",
            "System.Collections.Generic.Dictionary<TKey, TValue>" => "Dictionary",
            "System.Collections.Generic.KeyValuePair<TKey, TValue>" => "KeyValuePair",
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
        if (model.HasComplexBaseType || model.UseActivator || model.SerializationHooks.Count > 0)
            return "this context supports models with default construction, an object base, and generated member serialization";
        foreach (var member in model.Members)
        {
            if (!member.IsSerializable && !member.IsCopyable) continue;
            if (SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, services.Compilation.Assembly))
            {
                var generatedMember = new SerializerGenerator.SerializableMember(services, member);
                if (generatedMember.GetGetterFieldDescription() is { InitializationSyntax: not null }
                    || generatedMember.GetSetterFieldDescription() is { InitializationSyntax: not null })
                    return $"serialized member '{member.Symbol.Name}' requires statically generated field accessor support";
            }
            registration.Dependencies.Add(member.Type);
        }

        var definitionModel = named.IsGenericType
            ? SerializableSourceOutputGenerator.CreateSerializableTypeDescription(services, named.OriginalDefinition)!
            : model;
        DescribeGeneratedModel(registration, named, definitionModel);
        var substitutions = definitionModel.TypeParameters
            .Zip(named.GetAllTypeArguments(), static (parameter, argument) => (parameter.Parameter.Name, Type: argument.ToTypeSyntax()))
            .ToDictionary(static entry => entry.Name, static entry => entry.Type, StringComparer.Ordinal);
        var binding = services.Compilation.GetSemanticModel(services.Compilation.SyntaxTrees.First());
        var declarations = new List<ClassDeclarationSyntax> { new SerializerGenerator(services).Generate(definitionModel) };
        if (new CopierGenerator(services).GenerateCopier(definitionModel, new()) is { } copierDeclaration)
            declarations.Add(copierDeclaration);
        foreach (var request in declarations.SelectMany(static declaration => declaration.DescendantNodes().OfType<InvocationExpressionSyntax>())
            .Where(static invocation => invocation.Expression is MemberAccessExpressionSyntax { Name: GenericNameSyntax { Identifier.ValueText: "GetService" } })
            .Select(static invocation => ((GenericNameSyntax)((MemberAccessExpressionSyntax)invocation.Expression).Name).TypeArgumentList.Arguments.Single()))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var substituted = request.ReplaceNodes(request.DescendantNodesAndSelf().OfType<IdentifierNameSyntax>()
                .Where(identifier => substitutions.ContainsKey(identifier.Identifier.ValueText)),
                (original, _) => substitutions[original.Identifier.ValueText]);
            if (binding.GetSpeculativeTypeInfo(0, substituted, SpeculativeBindingOption.BindAsTypeOrNamespace).Type is INamedTypeSymbol service
                && (SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, services.LibraryTypes.ArrayCodec)
                    || SymbolEqualityComparer.Default.Equals(service.OriginalDefinition, services.LibraryTypes.ArrayCopier)))
            {
                var canonicalArray = services.Compilation.CreateArrayTypeSymbol(service.TypeArguments[0]);
                if (!registration.CanonicalArrays.Any(existing => SymbolEqualityComparer.Default.Equals(existing, canonicalArray)))
                    registration.CanonicalArrays.Add(canonicalArray);
                registration.Dependencies.Add(canonicalArray);
                registration.Dependencies.Add(canonicalArray.ElementType);
            }
        }

        if (!SymbolEqualityComparer.Default.Equals(named.ContainingAssembly, services.Compilation.Assembly))
        {
            var arity = named.TypeArguments.Length;
            var generatedNamespace = SerializerGenerator.GetGeneratedNamespaceName(named);
            registration.ReferencedCodec = ResolveImplementation(SerializerGenerator.GetSimpleClassName(named.Name));
            if (!definitionModel.IsShallowCopyable)
            {
                registration.ReferencedCopier = ResolveImplementation(CopierGenerator.GetSimpleClassName(named.Name));
            }

            if (registration.ReferencedCodec is null || !definitionModel.IsShallowCopyable && registration.ReferencedCopier is null)
            {
                return "provide the referenced assembly's generated codec and copier implementations";
            }


            INamedTypeSymbol? ResolveImplementation(string name)
            {
                var metadataName = $"{generatedNamespace}.{name}" + (arity > 0 ? $"`{arity}" : "");
                var implementation = services.Compilation.GetTypeByMetadataName(metadataName);
                return implementation is { IsGenericType: true } ? implementation.Construct([.. named.TypeArguments]) : implementation;
            }
        }

        return null;
    }

    private static void DescribeGeneratedModel(Registration registration, INamedTypeSymbol named, ISerializableTypeDescription model)
    {
        var argumentsSuffix = named.IsGenericType
            ? $"<{string.Join(", ", named.TypeArguments.Select(Name))}>"
            : "";
        var generatedNamespace = SerializerGenerator.GetGeneratedNamespaceName(named);
        registration.Codec = $"global::{generatedNamespace}.{SerializerGenerator.GetSimpleClassName(named.Name)}{argumentsSuffix}";
        registration.Copier = model.IsShallowCopyable
            ? $"global::Orleans.Serialization.Cloning.ShallowCopier<{Name(named)}>"
            : $"global::{generatedNamespace}.{CopierGenerator.GetSimpleClassName(named.Name)}{argumentsSuffix}";
        registration.Model = model;
    }

    private static bool TryGetTupleServices(IGeneratorServices services, INamedTypeSymbol type,
        [NotNullWhen(true)] out INamedTypeSymbol? codec, [NotNullWhen(true)] out INamedTypeSymbol? copier)
    {
        codec = null;
        copier = null;
        var arity = type.TypeArguments.Length;
        if (arity == 0)
        {
            return false;
        }

        var family = SymbolEqualityComparer.Default.Equals(type.OriginalDefinition,
            services.Compilation.GetTypeByMetadataName($"System.Tuple`{arity}")) ? "Tuple"
            : SymbolEqualityComparer.Default.Equals(type.OriginalDefinition,
                services.Compilation.GetTypeByMetadataName($"System.ValueTuple`{arity}")) ? "ValueTuple" : null;
        if (family is null) return false;
        codec = services.Compilation.GetTypeByMetadataName($"Orleans.Serialization.Codecs.{family}Codec`{arity}")!.Construct([.. type.TypeArguments]);
        copier = services.Compilation.GetTypeByMetadataName($"Orleans.Serialization.Codecs.{family}Copier`{arity}")!.Construct([.. type.TypeArguments]);
        return true;
    }

    private static bool TryGetConstructionCollectionServices(IGeneratorServices services, INamedTypeSymbol type,
        [NotNullWhen(true)] out INamedTypeSymbol? codec, [NotNullWhen(true)] out INamedTypeSymbol? copier)
    {
        codec = null;
        copier = null;
        var codecDefinition = services.LibraryTypes.WellKnownCodecs.FindByUnderlyingType(type.OriginalDefinition)?.CodecType;
        var copierDefinition = services.LibraryTypes.WellKnownCopiers.FindByUnderlyingType(type.OriginalDefinition)?.CopierType;
        if (codecDefinition is null || copierDefinition is null)
        {
            var name = type.OriginalDefinition.ToDisplayString() switch
            {
                "System.Collections.Generic.SortedDictionary<TKey, TValue>" => "SortedDictionary",
                "System.Collections.Generic.SortedList<TKey, TValue>" => "SortedList",
                "System.Collections.Generic.SortedSet<T>" => "SortedSet",
                "System.Collections.Generic.Queue<T>" => "Queue",
                "System.Collections.Generic.Stack<T>" => "Stack",
                "System.Collections.Concurrent.ConcurrentDictionary<TKey, TValue>" => "ConcurrentDictionary",
                "System.Collections.Concurrent.ConcurrentQueue<T>" => "ConcurrentQueue",
                "System.Collections.ObjectModel.Collection<T>" => "Collection",
                "System.Collections.ObjectModel.ReadOnlyCollection<T>" => "ReadOnlyCollection",
                "System.Collections.ObjectModel.ReadOnlyDictionary<TKey, TValue>" => "ReadOnlyDictionary",
                "System.Collections.Frozen.FrozenDictionary<TKey, TValue>" => "FrozenDictionary",
                "System.Collections.Frozen.FrozenSet<T>" => "FrozenSet",
                "System.ArraySegment<T>" => "ArraySegment",
                "System.Memory<T>" => "Memory",
                "System.ReadOnlyMemory<T>" => "ReadOnlyMemory",
                _ => null
            };
            if (name is null) return false;
            codecDefinition = services.Compilation.GetTypeByMetadataName($"Orleans.Serialization.Codecs.{name}Codec`{type.Arity}");
            copierDefinition = services.Compilation.GetTypeByMetadataName($"Orleans.Serialization.Codecs.{name}Copier`{type.Arity}");
        }
        if (codecDefinition is not { IsGenericType: true } || copierDefinition is not { IsGenericType: true }) return false;
        codec = codecDefinition.Construct([.. type.TypeArguments]);
        copier = copierDefinition.Construct([.. type.TypeArguments]);
        return true;
    }

    private static void ConstructGeneratedModel(Registration registration, IGeneratorServices services)
    {
        var codecDeclaration = new SerializerGenerator(services).Generate(registration.Model!);
        var copierDeclaration = new CopierGenerator(services).GenerateCopier(registration.Model!, new());
        registration.CodecConstruction = registration.ReferencedCodec is { } referencedCodec
            ? ConstructReferenced(registration.Codec, referencedCodec)
            : ConstructGenerated(registration.Codec, codecDeclaration);
        registration.CopierConstruction = copierDeclaration is null
            ? $"new {registration.Copier}()"
            : registration.ReferencedCopier is { } referencedCopier
                ? ConstructReferenced(registration.Copier, referencedCopier)
                : ConstructGenerated(registration.Copier, copierDeclaration);
    }

    private static string ConstructReferenced(string name, INamedTypeSymbol implementation, bool preferCompleteConstructor = false)
    {
        var constructor = GetReferencedConstructor(implementation, preferCompleteConstructor);
        var arguments = constructor.Parameters.Select(parameter =>
            parameter.Type.ToDisplayString() == "Orleans.Serialization.Serializers.ICodecProvider"
                ? "provider"
                : Resolve(Name(parameter.Type)));
        return $"new {name}({string.Join(", ", arguments)})";
    }

    private static IMethodSymbol GetReferencedConstructor(INamedTypeSymbol implementation, bool preferCompleteConstructor)
    {
        var constructors = implementation.InstanceConstructors.Where(static constructor => constructor.DeclaredAccessibility == Accessibility.Public);
        return preferCompleteConstructor
            ? constructors.OrderByDescending(static constructor => constructor.Parameters.Length).First()
            : constructors.Single();
    }

    private static string ConstructGenerated(string name, ClassDeclarationSyntax declaration)
    {
        var constructor = declaration.Members.OfType<ConstructorDeclarationSyntax>().SingleOrDefault();
        var arguments = constructor?.ParameterList.Parameters.Select(parameter =>
            parameter.Type!.ToString().EndsWith(".ICodecProvider", StringComparison.Ordinal)
                || parameter.Type.ToString() == "ICodecProvider"
                ? "provider"
                : Resolve(parameter.Type.ToString())) ?? [];
        return $"new {name}({string.Join(", ", arguments)})";
    }

    private static void AppendTypeMetadata(StringBuilder result, INamedTypeSymbol type, LibraryTypes library, HashSet<ITypeSymbol> visited)
    {
        if (!visited.Add(type)) return;
        var openType = type.ToOpenTypeSyntax().ToString();
        if (GeneratedCodeUtilities.GetAlias(library, type) is { } alias)
        {
            result.Append("options.WellKnownTypeAliases.TryAdd(").Append(alias.GetLiteralExpression())
                .Append(", typeof(").Append(openType).AppendLine("));");
        }

        if (GeneratedCodeUtilities.GetId(library, type) is { } id)
        {
            result.Append("options.WellKnownTypeIds.TryAdd(").Append(id).Append("U, typeof(").Append(openType).AppendLine("));");
        }

        foreach (var compound in type.GetAttributes().Where(attribute =>
            SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, library.CompoundTypeAliasAttribute)
            && attribute.ConstructorArguments.Length == 1))
        {
            var components = compound.ConstructorArguments[0].Values;
            foreach (var componentType in components.Select(component => component.Value).OfType<ITypeSymbol>())
            {
                result.Append("options.AddAllowedType(typeof(").Append(Name(componentType)).AppendLine("));");
                if (componentType is INamedTypeSymbol named)
                    AppendTypeMetadata(result, named, library, visited);
            }

            result.Append("options.CompoundTypeAliases");
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index].Value switch
                {
                    string value => value.GetLiteralExpression().ToString(),
                    ITypeSymbol symbol => $"typeof({Name(symbol)})",
                    _ => throw new InvalidOperationException("Compound aliases contain type or string components.")
                };
                result.Append(index == components.Length - 1 ? ".Add(" : ".GetOrAdd(").Append(component);
                if (index == components.Length - 1)
                    result.Append(", typeof(").Append(openType).Append(')');
                result.Append(')');
            }

            result.AppendLine(";");
        }
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

    private static string Resolve(string type, string caller = "null!")
        => $"global::Orleans.Serialization.GeneratedCodeHelpers.OrleansGeneratedCodeHelper.GetService<{type}>({caller}, provider)";

    private static string Name(ITypeSymbol type)
        => type.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

}
