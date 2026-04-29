using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class SerializableSourceOutputGenerator
{
    internal static SerializableTypeResult CreateSerializableTypeResult(
        GeneratorAttributeSyntaxContext context,
        SourceGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        if (context.TargetSymbol is not INamedTypeSymbol symbol)
        {
            return default;
        }

        var sourceLocation = ModelExtractor.GetSourceLocation(symbol);
        var metadataIdentity = TypeMetadataIdentity.Create(symbol);
        var typeSyntax = symbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            SourceGeneratorOptionsParser.AttachDebuggerIfRequested(options);

            var compilation = context.SemanticModel.Compilation;
            var codeGeneratorOptions = SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options);
            var libraryTypes = LibraryTypes.FromCompilation(compilation, codeGeneratorOptions);
            var typeDescription = CreateSerializableTypeDescription(compilation, libraryTypes, codeGeneratorOptions, symbol);
            if (typeDescription is null)
            {
                return default;
            }

            var model = ModelExtractor.ExtractSerializableTypeModel(typeDescription, sourceLocation);
            return SerializableTypeResult.FromModel(model);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return SerializableTypeResult.FromDiagnostic(
                analysisException.Diagnostic,
                metadataIdentity,
                sourceLocation,
                typeSyntax);
        }
    }

    internal static ImmutableArray<SourceOutputResult> CreateSerializableSourceOutputs(
        Compilation compilation,
        ImmutableArray<SerializableTypeModel> models,
        SourceGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        if (models.IsDefaultOrEmpty)
        {
            return [];
        }

        SourceGeneratorOptionsParser.AttachDebuggerIfRequested(options);
        var codeGeneratorOptions = SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options);
        var generatorServices = new GeneratorServices(compilation, codeGeneratorOptions);
        var resolver = new TypeSymbolResolver(compilation);
        var assemblyName = compilation.AssemblyName ?? "assembly";
        var sourceEntries = ImmutableArray.CreateBuilder<SourceOutputResult>();
        var defaultCopiers = new Dictionary<ISerializableTypeDescription, TypeSyntax>();
        var serializerGenerator = new SerializerGenerator(generatorServices);
        var copierGenerator = new CopierGenerator(generatorServices);
        var activatorGenerator = new ActivatorGenerator(generatorServices);

        foreach (var model in ModelExtractor.DeduplicateSerializableTypes(models))
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (!resolver.TryResolveSerializableType(model, cancellationToken, out var symbol)
                    || !SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly))
                {
                    continue;
                }

                var typeDescription = CreateSerializableTypeDescription(generatorServices, symbol);
                if (typeDescription is null)
                {
                    continue;
                }

                sourceEntries.Add(CreateSerializableSourceOutput(
                    assemblyName,
                    typeDescription,
                    serializerGenerator,
                    copierGenerator,
                    activatorGenerator,
                    defaultCopiers,
                    model.MetadataIdentity,
                    model.TypeSyntax.SyntaxString,
                    model.GeneratedNamespace,
                    model.TypeParameters.Length));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
            {
                sourceEntries.Add(SourceOutputResult.FromDiagnostic(analysisException.Diagnostic));
            }
        }

        return GeneratedSourceOutput.DeduplicateSourceOutputs(sourceEntries);
    }

    internal static ImmutableArray<SourceOutputResult> CreateReferencedSerializableSourceOutputs(
        Compilation compilation,
        ReferenceAssemblyModel referenceData,
        SourceGeneratorOptions options,
        CancellationToken cancellationToken)
    {
        try
        {
            if (referenceData is null || referenceData.ReferencedSerializableTypes.IsDefaultOrEmpty)
            {
                return [];
            }

            var processedModelTypes = new HashSet<string>(StringComparer.Ordinal);
            var modelsToResolve = ImmutableArray.CreateBuilder<SerializableTypeModel>();
            foreach (var model in referenceData.ReferencedSerializableTypes
                .Distinct()
                .OrderBy(static model => model.TypeSyntax.SyntaxString, StringComparer.Ordinal)
                .ThenBy(static model => model.MetadataIdentity.MetadataName, StringComparer.Ordinal)
                .ThenBy(static model => model.MetadataIdentity.AssemblyIdentity, StringComparer.Ordinal)
                .ThenBy(static model => model.MetadataIdentity.AssemblyName, StringComparer.Ordinal)
                .ThenBy(static model => model.GeneratedNamespace, StringComparer.Ordinal)
                .ThenBy(static model => model.Name, StringComparer.Ordinal))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsCurrentCompilationAssembly(model.MetadataIdentity, compilation))
                {
                    continue;
                }

                var modelTypeKey = $"{model.MetadataIdentity.AssemblyIdentity}|{model.MetadataIdentity.AssemblyName}|{model.MetadataIdentity.MetadataName}|{model.GeneratedNamespace}|{model.Name}|{model.TypeSyntax.SyntaxString}";
                if (processedModelTypes.Add(modelTypeKey))
                {
                    modelsToResolve.Add(model);
                }
            }

            if (modelsToResolve.Count == 0)
            {
                return [];
            }

            SourceGeneratorOptionsParser.AttachDebuggerIfRequested(options);
            var codeGeneratorOptions = SourceGeneratorOptionsParser.CreateCodeGeneratorOptions(options);
            var generatorServices = new GeneratorServices(compilation, codeGeneratorOptions);
            var resolver = new TypeSymbolResolver(compilation);
            var assemblyName = compilation.AssemblyName ?? "assembly";
            var sourceEntries = ImmutableArray.CreateBuilder<SourceOutputResult>();
            var defaultCopiers = new Dictionary<ISerializableTypeDescription, TypeSyntax>();
            var serializerGenerator = new SerializerGenerator(generatorServices);
            var copierGenerator = new CopierGenerator(generatorServices);
            var activatorGenerator = new ActivatorGenerator(generatorServices);

            foreach (var model in modelsToResolve)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!resolver.TryResolveSerializableType(model, cancellationToken, out var symbol)
                    || SymbolEqualityComparer.Default.Equals(symbol.ContainingAssembly, compilation.Assembly))
                {
                    continue;
                }

                var typeDescription = CreateSerializableTypeDescription(generatorServices, symbol);
                if (typeDescription is null)
                {
                    continue;
                }

                sourceEntries.Add(CreateSerializableSourceOutput(
                    assemblyName,
                    typeDescription,
                    serializerGenerator,
                    copierGenerator,
                    activatorGenerator,
                    defaultCopiers,
                    model.MetadataIdentity,
                    model.TypeSyntax.SyntaxString,
                    model.GeneratedNamespace,
                    model.TypeParameters.Length));
            }

            return GeneratedSourceOutput.DeduplicateSourceOutputs(sourceEntries);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OrleansGeneratorDiagnosticAnalysisException analysisException)
        {
            return [SourceOutputResult.FromDiagnostic(analysisException.Diagnostic)];
        }
    }

    internal static SourceOutputResult CreateSerializableSourceOutput(
        string assemblyName,
        ISerializableTypeDescription typeDescription,
        SerializerGenerator serializerGenerator,
        CopierGenerator copierGenerator,
        ActivatorGenerator activatorGenerator,
        Dictionary<ISerializableTypeDescription, TypeSyntax> defaultCopiers,
        TypeMetadataIdentity metadataIdentity,
        string typeSyntax,
        string hintGeneratedNamespace,
        int genericArity)
    {
        var serializer = serializerGenerator.Generate(typeDescription);
        var copier = typeDescription.IsShallowCopyable && defaultCopiers.ContainsKey(typeDescription)
            ? null
            : copierGenerator.GenerateCopier(typeDescription, defaultCopiers);
        var activatorClass = ActivatorGenerator.ShouldGenerateActivator(typeDescription)
            ? activatorGenerator.GenerateActivator(typeDescription)
            : null;

        return SourceOutputResult.FromSource(
            GeneratedSourceOutput.CreateSerializableSourceEntry(
                assemblyName,
                typeSyntax,
                metadataIdentity,
                hintGeneratedNamespace,
                genericArity,
                serializer,
                copier,
                activatorClass,
                typeDescription.GeneratedNamespace));
    }

    internal static ISerializableTypeDescription? CreateSerializableTypeDescription(IGeneratorServices services, INamedTypeSymbol symbol)
        => CreateSerializableTypeDescription(services.Compilation, services.LibraryTypes, services.Options, symbol);

    internal static ISerializableTypeDescription? CreateSerializableTypeDescription(Compilation compilation, LibraryTypes libraryTypes, CodeGeneratorOptions options, INamedTypeSymbol symbol)
    {

        if (FSharpUtilities.IsUnionCase(libraryTypes, symbol, out var sumType) && sumType.HasAttribute(libraryTypes.GenerateSerializerAttribute))
        {
            if (!compilation.IsSymbolAccessibleWithin(sumType, compilation.Assembly))
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(sumType));
            }

            return new FSharpUtilities.FSharpUnionCaseTypeDescription(compilation, symbol, libraryTypes);
        }

        if (!symbol.HasAttribute(libraryTypes.GenerateSerializerAttribute))
        {
            return null;
        }

        if (HasReferenceAssemblyAttribute(symbol.ContainingAssembly))
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(ReferenceAssemblyWithGenerateSerializerDiagnostic.CreateDiagnostic(symbol));
        }

        if (!compilation.IsSymbolAccessibleWithin(symbol, compilation.Assembly))
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(symbol));
        }

        if (FSharpUtilities.IsRecord(libraryTypes, symbol))
        {
            return new FSharpUtilities.FSharpRecordTypeDescription(compilation, symbol, libraryTypes);
        }

        var includePrimaryConstructorParameters = ShouldIncludePrimaryConstructorParameters(symbol, libraryTypes);
        var constructorParameters = ResolveConstructorParameters(symbol, includePrimaryConstructorParameters, libraryTypes);
        var implicitMemberSelectionStrategy = (options.GenerateFieldIds, GetGenerateFieldIdsOptionFromType(symbol, libraryTypes)) switch
        {
            (_, GenerateFieldIds.PublicProperties) => GenerateFieldIds.PublicProperties,
            (GenerateFieldIds.PublicProperties, _) => GenerateFieldIds.PublicProperties,
            _ => GenerateFieldIds.None,
        };
        var fieldIdAssignmentHelper = new FieldIdAssignmentHelper(symbol, constructorParameters, implicitMemberSelectionStrategy, libraryTypes);
        if (!fieldIdAssignmentHelper.IsValidForSerialization)
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(CanNotGenerateImplicitFieldIdsDiagnostic.CreateDiagnostic(symbol, fieldIdAssignmentHelper.FailureReason!));
        }

        return new SerializableTypeDescription(compilation, symbol, includePrimaryConstructorParameters, GetDataMembers(fieldIdAssignmentHelper), libraryTypes);
    }

    internal static bool HasReferenceAssemblyAttribute(IAssemblySymbol assembly)
    {
        return assembly.GetAttributes().Any(attributeData => attributeData.AttributeClass is
        {
            Name: "ReferenceAssemblyAttribute",
            ContainingNamespace:
            {
                Name: "CompilerServices",
                ContainingNamespace:
                {
                    Name: "Runtime",
                    ContainingNamespace:
                    {
                        Name: "System",
                        ContainingNamespace.IsGlobalNamespace: true,
                    },
                },
            },
        });
    }

    internal static GenerateFieldIds GetGenerateFieldIdsOptionFromType(INamedTypeSymbol type, LibraryTypes libraryTypes)
    {
        var attribute = type.GetAttribute(libraryTypes.GenerateSerializerAttribute);
        if (attribute is null)
        {
            return GenerateFieldIds.None;
        }

        foreach (var namedArgument in attribute.NamedArguments)
        {
            if (namedArgument.Key == "GenerateFieldIds")
            {
                var value = namedArgument.Value.Value;
                return value is null ? GenerateFieldIds.None : (GenerateFieldIds)(int)value;
            }
        }

        return GenerateFieldIds.None;
    }

    internal static bool ShouldIncludePrimaryConstructorParameters(INamedTypeSymbol type, LibraryTypes libraryTypes)
    {
        static bool? GetNamedOption(INamedTypeSymbol type, INamedTypeSymbol attributeType)
        {
            var attribute = type.GetAttribute(attributeType);
            if (attribute is null)
            {
                return null;
            }

            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == "IncludePrimaryConstructorParameters"
                    && namedArgument.Value.Kind == TypedConstantKind.Primitive
                    && namedArgument.Value.Value is bool value)
                {
                    return value;
                }
            }

            return null;
        }

        if (GetNamedOption(type, libraryTypes.GenerateSerializerAttribute) is bool includePrimaryCtorParameters)
        {
            return includePrimaryCtorParameters;
        }

        if (type.IsRecord)
        {
            return true;
        }

        var properties = type.GetMembers().OfType<IPropertySymbol>().ToImmutableArray();
        return type.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(static method => method.MethodKind == MethodKind.Constructor && method.Parameters.Length > 0)
            .Any(ctor => ctor.Parameters.All(parameter =>
                properties.Any(property => property.Name.Equals(parameter.Name, StringComparison.Ordinal) && property.IsCompilerGenerated())));
    }

    internal static ImmutableArray<IParameterSymbol> ResolveConstructorParameters(
        INamedTypeSymbol type,
        bool includePrimaryConstructorParameters,
        LibraryTypes libraryTypes)
    {
        if (!includePrimaryConstructorParameters)
        {
            return [];
        }

        if (type.IsRecord)
        {
            var potentialPrimaryConstructor = type.Constructors[0];
            if (!potentialPrimaryConstructor.IsImplicitlyDeclared && !potentialPrimaryConstructor.IsCompilerGenerated())
            {
                return potentialPrimaryConstructor.Parameters;
            }
        }
        else
        {
            var annotatedConstructors = type.Constructors.Where(ctor => ctor.HasAnyAttribute(libraryTypes.ConstructorAttributeTypes)).ToList();
            if (annotatedConstructors.Count == 1)
            {
                return annotatedConstructors[0].Parameters;
            }

            var properties = type.GetMembers().OfType<IPropertySymbol>().ToImmutableArray();
            var primaryConstructor = type.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(static method => method.MethodKind == MethodKind.Constructor && method.Parameters.Length > 0)
                .FirstOrDefault(ctor => ctor.Parameters.All(parameter =>
                    properties.Any(property => property.Name.Equals(parameter.Name, StringComparison.Ordinal) && property.IsCompilerGenerated())));
            if (primaryConstructor is not null)
            {
                return primaryConstructor.Parameters;
            }
        }

        return [];
    }

    internal static IEnumerable<IMemberDescription> GetDataMembers(FieldIdAssignmentHelper fieldIdAssignmentHelper)
    {
        var members = new Dictionary<(uint Id, bool IsConstructorParameter), IMemberDescription>();
        foreach (var member in fieldIdAssignmentHelper.Members)
        {
            if (!fieldIdAssignmentHelper.TryGetSymbolKey(member, out var key))
            {
                continue;
            }

            var (id, isConstructorParameter) = key;
            if (member is IPropertySymbol property
                && !members.TryGetValue((id, isConstructorParameter), out _))
            {
                members[(id, isConstructorParameter)] = new PropertyDescription(id, isConstructorParameter, property);
            }

            if (member is IFieldSymbol field)
            {
                if (!members.TryGetValue((id, isConstructorParameter), out var existing)
                    || existing is PropertyDescription)
                {
                    members[(id, isConstructorParameter)] = new FieldDescription(id, isConstructorParameter, field);
                }
            }
        }

        return members.Values;
    }

    internal static bool IsCurrentCompilationAssembly(TypeMetadataIdentity metadataIdentity, Compilation compilation)
    {
        if (metadataIdentity.IsEmpty)
        {
            return false;
        }

        var assemblyIdentity = compilation.Assembly.Identity;
        if (!string.IsNullOrEmpty(metadataIdentity.AssemblyIdentity))
        {
            return string.Equals(metadataIdentity.AssemblyIdentity, assemblyIdentity.GetDisplayName(), StringComparison.Ordinal);
        }

        return !string.IsNullOrEmpty(metadataIdentity.AssemblyName)
            && string.Equals(metadataIdentity.AssemblyName, assemblyIdentity.Name, StringComparison.Ordinal);
    }
}



