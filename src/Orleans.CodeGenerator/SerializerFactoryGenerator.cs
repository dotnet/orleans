using System.Text;
using System.Diagnostics.CodeAnalysis;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
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
            if (Describe(registration, services, implementationCompilation, cancellationToken) is { } reason)
            {
                failure = new(type, reason);
                return false;
            }
            registrations.Add(type, registration);
            foreach (var dependency in registration.Dependencies) pending.Enqueue(dependency);
        }

        var serializerGenerator = new SerializerGenerator(services);
        var copierGenerator = new CopierGenerator(services);
        var result = new StringBuilder();
        var metadataTypes = new HashSet<ITypeSymbol>(SymbolEqualityComparer.Default);
        var auxiliaryServices = new HashSet<string>(StringComparer.Ordinal);

        foreach (var registration in registrations.Values.OrderBy(value => Name(value.Type), StringComparer.Ordinal))
        {
            var typeName = Name(registration.Type);
            var hasBaseCodec = false;
            var hasBaseCopier = false;
            if (registration.Model is { } model)
            {
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
                else
                {
                    var codecDeclaration = serializerGenerator.Generate(model);
                    var copierDeclaration = copierGenerator.GenerateCopier(model, new());
                    hasBaseCodec = codecDeclaration.BaseList!.Types.Any(contract =>
                        contract.Type.ToString() == services.LibraryTypes.BaseCodec_1.ToTypeSyntax(model.TypeSyntax).ToString());
                    hasBaseCopier = copierDeclaration?.BaseList?.Types.Any(contract =>
                        contract.Type.ToString() == services.LibraryTypes.BaseCopier_1.ToTypeSyntax(model.TypeSyntax).ToString()) == true;
                    registration.CodecConstruction = ConstructGenerated(registration.Codec, codecDeclaration);
                    registration.CopierConstruction = copierDeclaration is null
                        ? $"new {registration.Copier}()"
                        : ConstructGenerated(registration.Copier, copierDeclaration);
                }
            }
            else if (registration.Dependencies.Count > 0)
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

            result.Append("options.AddSerializerService<").Append(registration.Codec).Append(">(static provider => ")
                .Append(registration.CodecConstruction).AppendLine(");");
            result.Append("options.AddSerializerService<").Append(registration.Copier).Append(">(static provider => ")
                .Append(registration.CopierConstruction).AppendLine(");");
            result.Append("options.AddSerializer<").Append(typeName).Append(">(static provider => ")
                .Append(Resolve(registration.Codec)).Append(", static provider => ").Append(Resolve(registration.Copier)).AppendLine(");");
            if (registration.Model is { IsValueType: true, IsEnumType: false })
            {
                result.Append("options.AddSerializerService<global::Orleans.Serialization.Serializers.IValueSerializer<")
                    .Append(typeName).Append(">>(static provider => ").Append(Resolve(registration.Codec)).AppendLine(");");
            }
            if (hasBaseCodec)
            {
                result.Append("options.AddSerializerService<global::Orleans.Serialization.Serializers.IBaseCodec<")
                    .Append(typeName).Append(">>(static provider => ").Append(Resolve(registration.Codec)).AppendLine(");");
            }
            if (hasBaseCopier)
            {
                result.Append("options.AddSerializerService<global::Orleans.Serialization.Cloning.IBaseCopier<")
                    .Append(typeName).Append(">>(static provider => ").Append(Resolve(registration.Copier)).AppendLine(");");
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
                    result.Append("options.AddSerializerService<").Append(canonicalCodec).Append(">(static provider => new ")
                        .Append(canonicalCodec).Append('(').Append(codecDependency).AppendLine("));");
                }
                if (arrayRegistration.Copier != canonicalCopier && auxiliaryServices.Add(canonicalCopier))
                {
                    var copierDependency = cyclic ? $"CreateCopierHolder<{Name(array.ElementType)}>(provider)" : $"provider.GetDeepCopier<{Name(array.ElementType)}>()";
                    result.Append("options.AddSerializerService<").Append(canonicalCopier).Append(">(static provider => new ")
                        .Append(canonicalCopier).Append('(').Append(copierDependency).AppendLine("));");
                }
            }

            result.Append("options.AddAllowedType(typeof(").Append(typeName).AppendLine("));");
            if (AppendTypeMetadata(result, registration.Type, services.LibraryTypes, metadataTypes) is { } metadataFailure)
            {
                failure = metadataFailure;
                return false;
            }
        }

        graph = new Graph(registrations, result.ToString());
        return true;
    }

    private static string? Describe(Registration registration, IGeneratorServices services, Compilation implementationCompilation, CancellationToken cancellationToken)
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
            return DescribeReferenced(registration, named, services, implementationCompilation, cancellationToken);
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
                .Where(identifier => identifier.Parent is not QualifiedNameSyntax and not AliasQualifiedNameSyntax
                    && substitutions.ContainsKey(identifier.Identifier.ValueText)),
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

        return null;
    }

    private static string? DescribeReferenced(
        Registration registration, INamedTypeSymbol named, IGeneratorServices services,
        Compilation implementationCompilation, CancellationToken cancellationToken)
    {
        var binding = implementationCompilation.GetSemanticModel(implementationCompilation.SyntaxTrees.First());
        var implementationType = binding.GetSpeculativeTypeInfo(0, named.ToTypeSyntax(),
            SpeculativeBindingOption.BindAsTypeOrNamespace).Type as INamedTypeSymbol;
        if (implementationType is null) return "provide the referenced model's implementation metadata";
        var library = LibraryTypes.FromCompilation(implementationCompilation, services.Options);
        var model = new SerializableTypeDescription(implementationCompilation, implementationType,
            SerializableSourceOutputGenerator.ShouldIncludePrimaryConstructorParameters(implementationType, library), [], library);
        if (model.HasComplexBaseType || model.UseActivator || model.SerializationHooks.Count > 0)
            return "this context supports models with default construction, an object base, and generated member serialization";
        registration.Model = model;
        registration.ReferencedCodec = ResolveImplementation(SerializerGenerator.GetSimpleClassName(named.Name));
        registration.ReferencedCopier = ResolveImplementation(CopierGenerator.GetSimpleClassName(named.Name));
        if (registration.ReferencedCodec is null || registration.ReferencedCopier is null && !model.IsShallowCopyable)
            return "provide the referenced assembly's generated codec and copier implementations";
        registration.Codec = Name(registration.ReferencedCodec);
        registration.Copier = registration.ReferencedCopier is { } referencedCopier
            ? Name(referencedCopier)
            : Name(services.LibraryTypes.ShallowCopier.Construct(named));

        var consumerBinding = services.Compilation.GetSemanticModel(services.Compilation.SyntaxTrees.First());
        foreach (var implementation in new[] { registration.ReferencedCodec, registration.ReferencedCopier })
        {
            if (implementation is null) continue;
            cancellationToken.ThrowIfCancellationRequested();
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

    private static Failure? AppendTypeMetadata(StringBuilder result, ITypeSymbol symbol, LibraryTypes library, HashSet<ITypeSymbol> visited, int depth = 0)
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
                    result.Append("options.AddAllowedType(typeof(").Append(Name(argument)).AppendLine("));");
                if (AppendTypeMetadata(result, argument, library, visited, depth + 1) is { } argumentFailure) return argumentFailure;
            }
        }
        var openType = type.ToOpenTypeSyntax().ToString();
        if (type.HasAttribute(library.AliasAttribute) || type.HasAttribute(library.CompoundTypeAliasAttribute)
            || GeneratedCodeUtilities.GetId(library, type) is not null)
        {
            result.Append("options.AddAllowedType(typeof(").Append(openType).AppendLine("));");
        }
        if (GeneratedCodeUtilities.GetAlias(library, type) is { } alias)
        {
            result.AppendLine(MetadataGenerator.CreateTypeMetadataRegistration(
                SyntaxFactory.ParseExpression("options.WellKnownTypeAliases"), alias.GetLiteralExpression(),
                SyntaxFactory.ParseExpression($"typeof({openType})")).NormalizeWhitespace().ToFullString());
        }

        if (GeneratedCodeUtilities.GetId(library, type) is { } id)
        {
            result.AppendLine(MetadataGenerator.CreateTypeMetadataRegistration(
                SyntaxFactory.ParseExpression("options.WellKnownTypeIds"), SyntaxFactory.ParseExpression($"{id}U"),
                SyntaxFactory.ParseExpression($"typeof({openType})")).NormalizeWhitespace().ToFullString());
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
                result.Append("options.AddAllowedType(typeof(").Append(Name(componentType)).AppendLine("));");
                if (AppendTypeMetadata(result, componentType, library, visited, depth + 1) is { } componentFailure) return componentFailure;
            }

            result.Append("options.CompoundTypeAliases");
            for (var index = 0; index < components.Length; index++)
            {
                var component = components[index].Value switch
                {
                    string value => value.GetLiteralExpression().ToString(),
                    ITypeSymbol componentSymbol => $"typeof({Name(componentSymbol)})",
                    _ => throw new InvalidOperationException("Compound aliases contain type or string components.")
                };
                result.Append(index == components.Length - 1 ? ".Add(" : ".GetOrAdd(").Append(component);
                if (index == components.Length - 1)
                    result.Append(", typeof(").Append(openType).Append(')');
                result.Append(')');
            }

            result.AppendLine(";");
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
