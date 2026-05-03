using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.SyntaxGeneration;

namespace Orleans.CodeGenerator;

internal static class SerializableTypeModelExtractor
{
    /// <summary>
    /// Extracts a <see cref="SerializableTypeModel"/> from an <see cref="ISerializableTypeDescription"/>.
    /// Converts symbol-based descriptions into equatable value models for incremental pipeline caching.
    /// </summary>
    internal static SerializableTypeModel ExtractSerializableTypeModel(
        ISerializableTypeDescription description,
        SourceLocationModel sourceLocation = default)
    {
        var typeParameters = ExtractTypeParameters(description.TypeParameters);
        var members = ExtractMembers(description.Members);
        var serializationHooks = ExtractTypeRefs(description.SerializationHooks);
        var activatorCtorParams = ExtractTypeRefSyntaxList(description.ActivatorConstructorParameters);
        var creationStrategy = DetermineCreationStrategy(description);

        return new SerializableTypeModel(
            Accessibility: description.Accessibility,
            TypeSyntax: new TypeRef(description.TypeSyntax.ToString()),
            HasComplexBaseType: description.HasComplexBaseType,
            IncludePrimaryConstructorParameters: description.IncludePrimaryConstructorParameters,
            BaseTypeSyntax: description.HasComplexBaseType ? new TypeRef(description.BaseTypeSyntax.ToString()) : TypeRef.Empty,
            Namespace: description.Namespace ?? string.Empty,
            GeneratedNamespace: description.GeneratedNamespace ?? string.Empty,
            Name: description.Name ?? string.Empty,
            IsValueType: description.IsValueType,
            IsSealedType: description.IsSealedType,
            IsAbstractType: description.IsAbstractType,
            IsEnumType: description.IsEnumType,
            IsGenericType: description.IsGenericType,
            TypeParameters: typeParameters,
            Members: members,
            UseActivator: description.UseActivator,
            IsEmptyConstructable: description.IsEmptyConstructable,
            HasActivatorConstructor: description.HasActivatorConstructor,
            TrackReferences: description.TrackReferences,
            OmitDefaultMemberValues: description.OmitDefaultMemberValues,
            SerializationHooks: serializationHooks,
            IsShallowCopyable: description.IsShallowCopyable,
            IsUnsealedImmutable: description.IsUnsealedImmutable,
            IsImmutable: description.IsImmutable,
            IsExceptionType: description.IsExceptionType,
            ActivatorConstructorParameters: activatorCtorParams,
            CreationStrategy: creationStrategy,
            SourceLocation: sourceLocation,
            MetadataIdentity: description is SerializableTypeDescription serializableDescription
                ? TypeMetadataIdentity.Create(serializableDescription.Type)
                : TypeMetadataIdentity.Empty);
    }

    private static ImmutableArray<TypeParameterModel> ExtractTypeParameters(
        List<(string Name, ITypeParameterSymbol Parameter)> typeParameters)
    {
        if (typeParameters is null || typeParameters.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TypeParameterModel>(typeParameters.Count);
        for (var i = 0; i < typeParameters.Count; i++)
        {
            var (name, param) = typeParameters[i];
            builder.Add(new TypeParameterModel(name, param.Name, param.Ordinal));
        }
        return builder.MoveToImmutable();
    }

    private static ImmutableArray<MemberModel> ExtractMembers(List<IMemberDescription> members)
    {
        if (members is null || members.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<MemberModel>(members.Count);
        foreach (var member in members)
        {
            builder.Add(ExtractMember(member));
        }
        return builder.MoveToImmutable();
    }

    private static MemberModel ExtractMember(IMemberDescription member)
    {
        var kind = member is IFieldDescription ? MemberKind.Field : MemberKind.Property;
        var symbol = member.Symbol;
        var containingType = member.ContainingType;

        // Determine getter/setter accessibility strategies
        var getterStrategy = DetermineGetterStrategy(member);
        var setterStrategy = DetermineSetterStrategy(member);

        // Determine if member has immutable attribute
        var hasImmutableAttribute = false;
        if (symbol is IPropertySymbol prop)
        {
            hasImmutableAttribute = prop.GetAttributes().Any(a =>
                a.AttributeClass?.Name == "ImmutableAttribute"
                && a.AttributeClass.ContainingNamespace?.Name == "Orleans");
        }
        if (!hasImmutableAttribute)
        {
            hasImmutableAttribute = symbol.GetAttributes().Any(a =>
                a.AttributeClass?.Name == "ImmutableAttribute"
                && a.AttributeClass.ContainingNamespace?.Name == "Orleans");
        }

        // Determine if obsolete
        var isObsolete = symbol.GetAttributes().Any(a =>
            a.AttributeClass?.Name == "ObsoleteAttribute"
            && a.AttributeClass.ContainingNamespace?.Name == "System");

        // Backing property name
        string? backingPropertyName = null;
        if (member is IFieldDescription fieldDesc)
        {
            var backingProp = PropertyUtility.GetMatchingProperty(fieldDesc.Field);
            if (backingProp is not null)
            {
                backingPropertyName = backingProp.Name;
            }
        }

        return new MemberModel(
            fieldId: member.FieldId,
            name: symbol.Name,
            type: new TypeRef(member.TypeSyntax.ToString()),
            containingType: containingType is not null ? new TypeRef(containingType.ToTypeSyntax().ToString()) : TypeRef.Empty,
            assemblyName: member.AssemblyName ?? string.Empty,
            typeNameIdentifier: member.TypeNameIdentifier ?? string.Empty,
            isPrimaryConstructorParameter: member.IsPrimaryConstructorParameter,
            isSerializable: member.IsSerializable,
            isCopyable: member.IsCopyable,
            kind: kind,
            getterStrategy: getterStrategy,
            setterStrategy: setterStrategy,
            isObsolete: isObsolete,
            hasImmutableAttribute: hasImmutableAttribute,
            isShallowCopyable: false, // Will be resolved later with LibraryTypes
            isValueType: member.Type?.IsValueType ?? false,
            containingTypeIsValueType: containingType?.IsValueType ?? false,
            backingPropertyName: backingPropertyName);
    }

    private static AccessStrategy DetermineGetterStrategy(IMemberDescription member)
    {
        if (member is IFieldDescription fieldDesc)
        {
            // Direct access if field is accessible
            return AccessStrategy.Direct;
        }

        if (member.Symbol is IPropertySymbol prop && prop.GetMethod is not null)
        {
            return AccessStrategy.Direct;
        }

        return AccessStrategy.GeneratedAccessor;
    }

    private static AccessStrategy DetermineSetterStrategy(IMemberDescription member)
    {
        if (member is IFieldDescription fieldDesc)
        {
            if (!fieldDesc.Field.IsReadOnly)
            {
                return AccessStrategy.Direct;
            }
            return AccessStrategy.GeneratedAccessor;
        }

        if (member.Symbol is IPropertySymbol prop)
        {
            if (prop.SetMethod is not null && !prop.SetMethod.IsInitOnly)
            {
                return AccessStrategy.Direct;
            }
            if (member.IsPrimaryConstructorParameter)
            {
                return AccessStrategy.UnsafeAccessor;
            }
            return AccessStrategy.GeneratedAccessor;
        }

        return AccessStrategy.GeneratedAccessor;
    }

    private static ImmutableArray<TypeRef> ExtractTypeRefs(List<INamedTypeSymbol>? symbols)
    {
        if (symbols is null || symbols.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TypeRef>(symbols.Count);
        foreach (var s in symbols)
        {
            builder.Add(new TypeRef(s.ToTypeSyntax().ToString()));
        }
        return builder.MoveToImmutable();
    }

    private static ImmutableArray<TypeRef> ExtractTypeRefSyntaxList(
        List<Microsoft.CodeAnalysis.CSharp.Syntax.TypeSyntax>? syntaxList)
    {
        if (syntaxList is null || syntaxList.Count == 0)
        {
            return [];
        }

        var builder = ImmutableArray.CreateBuilder<TypeRef>(syntaxList.Count);
        foreach (var ts in syntaxList)
        {
            builder.Add(new TypeRef(ts.ToString()));
        }
        return builder.MoveToImmutable();
    }

    private static ObjectCreationStrategy DetermineCreationStrategy(ISerializableTypeDescription description)
    {
        if (description.IsValueType)
        {
            return ObjectCreationStrategy.Default;
        }

        // Check if we can determine from the existing expression
        var expr = description.GetObjectCreationExpression();
        if (expr is Microsoft.CodeAnalysis.CSharp.Syntax.DefaultExpressionSyntax)
        {
            return ObjectCreationStrategy.Default;
        }

        if (expr is Microsoft.CodeAnalysis.CSharp.Syntax.ObjectCreationExpressionSyntax)
        {
            return ObjectCreationStrategy.NewExpression;
        }

        return ObjectCreationStrategy.GetUninitializedObject;
    }

    internal static SerializableTypeModel? TryExtractSerializableTypeModel(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        LibraryTypes libraryTypes,
        CodeGeneratorOptions options,
        bool throwOnFailure = false)
    {
        if (typeSymbol is null)
        {
            return null;
        }

        if (FSharpUtilities.IsUnionCase(libraryTypes, typeSymbol, out var sumType))
        {
            if (!sumType.HasAttribute(libraryTypes.GenerateSerializerAttribute))
            {
                return null;
            }

            if (throwOnFailure && HasReferenceAssemblyAttribute(sumType.ContainingAssembly))
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(
                    ReferenceAssemblyWithGenerateSerializerDiagnostic.CreateDiagnostic(sumType, Location.None));
            }

            if (!compilation.IsSymbolAccessibleWithin(sumType, compilation.Assembly))
            {
                if (throwOnFailure)
                {
                    throw new OrleansGeneratorDiagnosticAnalysisException(
                        InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(sumType, Location.None));
                }

                return null;
            }

            var fsharpUnionCaseDescription = new FSharpUtilities.FSharpUnionCaseTypeDescription(compilation, typeSymbol, libraryTypes);
            return ExtractSerializableTypeModel(fsharpUnionCaseDescription, SymbolSourceLocationExtractor.GetSourceLocation(typeSymbol));
        }

        if (!typeSymbol.HasAttribute(libraryTypes.GenerateSerializerAttribute))
        {
            return null;
        }

        if (throwOnFailure && HasReferenceAssemblyAttribute(typeSymbol.ContainingAssembly))
        {
            throw new OrleansGeneratorDiagnosticAnalysisException(
                ReferenceAssemblyWithGenerateSerializerDiagnostic.CreateDiagnostic(typeSymbol, Location.None));
        }

        if (!compilation.IsSymbolAccessibleWithin(typeSymbol, compilation.Assembly))
        {
            if (throwOnFailure)
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(
                    InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(typeSymbol, Location.None));
            }

            return null;
        }

        if (FSharpUtilities.IsRecord(libraryTypes, typeSymbol))
        {
            var fsharpDescription = new FSharpUtilities.FSharpRecordTypeDescription(compilation, typeSymbol, libraryTypes);
            return ExtractSerializableTypeModel(fsharpDescription, SymbolSourceLocationExtractor.GetSourceLocation(typeSymbol));
        }

        var includePrimaryCtorParams = GetIncludePrimaryConstructorParameters(typeSymbol, libraryTypes);
        var ctorParams = ResolveConstructorParameters(typeSymbol, includePrimaryCtorParams, libraryTypes);
        var implicitFieldIdStrategy = (options.GenerateFieldIds, GetFieldIdsOptionFromType(typeSymbol, libraryTypes)) switch
        {
            (_, GenerateFieldIds.PublicProperties) => GenerateFieldIds.PublicProperties,
            (GenerateFieldIds.PublicProperties, _) => GenerateFieldIds.PublicProperties,
            _ => GenerateFieldIds.None,
        };
        var helper = new FieldIdAssignmentHelper(typeSymbol, ctorParams, implicitFieldIdStrategy, libraryTypes);
        if (!helper.IsValidForSerialization)
        {
            if (throwOnFailure)
            {
                throw new OrleansGeneratorDiagnosticAnalysisException(
                    CanNotGenerateImplicitFieldIdsDiagnostic.CreateDiagnostic(typeSymbol, helper.FailureReason!, Location.None));
            }

            return null;
        }

        var members = CollectDataMembers(helper);
        var description = new SerializableTypeDescription(compilation, typeSymbol, includePrimaryCtorParams, members, libraryTypes);
        return ExtractSerializableTypeModel(description, SymbolSourceLocationExtractor.GetSourceLocation(typeSymbol));
    }

    private static bool HasReferenceAssemblyAttribute(IAssemblySymbol assembly)
    {
        return assembly?.GetAttributes().Any(attributeData => attributeData.AttributeClass is
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
        }) == true;
    }

    private static bool GetIncludePrimaryConstructorParameters(INamedTypeSymbol typeSymbol, LibraryTypes libraryTypes)
    {
        var attribute = typeSymbol.GetAttribute(libraryTypes.GenerateSerializerAttribute);
        if (attribute is not null)
        {
            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == "IncludePrimaryConstructorParameters"
                    && namedArgument.Value.Kind == TypedConstantKind.Primitive
                    && namedArgument.Value.Value is bool b)
                {
                    return b;
                }
            }
        }

        // Default to true for records
        if (typeSymbol.IsRecord)
        {
            return true;
        }

        // Detect primary constructor via compiler-generated properties
        var properties = typeSymbol.GetMembers().OfType<IPropertySymbol>().ToImmutableArray();
        return typeSymbol.GetMembers()
            .OfType<IMethodSymbol>()
            .Where(m => m.MethodKind == MethodKind.Constructor && m.Parameters.Length > 0)
            .Any(ctor => ctor.Parameters.All(prm =>
                properties.Any(prop => prop.Name.Equals(prm.Name, StringComparison.Ordinal) && prop.IsCompilerGenerated())));
    }

    private static ImmutableArray<IParameterSymbol> ResolveConstructorParameters(
        INamedTypeSymbol typeSymbol,
        bool includePrimaryCtorParams,
        LibraryTypes libraryTypes)
    {
        if (!includePrimaryCtorParams)
        {
            return [];
        }

        if (typeSymbol.IsRecord)
        {
            // Primary constructor is declared before the copy constructor for records
            var potentialPrimaryConstructor = typeSymbol.Constructors[0];
            if (!potentialPrimaryConstructor.IsImplicitlyDeclared && !potentialPrimaryConstructor.IsCompilerGenerated())
            {
                return potentialPrimaryConstructor.Parameters;
            }
        }
        else
        {
            var annotatedConstructors = typeSymbol.Constructors
                .Where(ctor => ctor.HasAnyAttribute(libraryTypes.ConstructorAttributeTypes))
                .ToList();
            if (annotatedConstructors.Count == 1)
            {
                return annotatedConstructors[0].Parameters;
            }

            // Fallback: detect primary constructor via compiler-generated properties
            var properties = typeSymbol.GetMembers().OfType<IPropertySymbol>().ToImmutableArray();
            var primaryConstructor = typeSymbol.GetMembers()
                .OfType<IMethodSymbol>()
                .Where(m => m.MethodKind == MethodKind.Constructor && m.Parameters.Length > 0)
                .FirstOrDefault(ctor => ctor.Parameters.All(prm =>
                    properties.Any(prop => prop.Name.Equals(prm.Name, StringComparison.Ordinal) && prop.IsCompilerGenerated())));

            if (primaryConstructor is not null)
            {
                return primaryConstructor.Parameters;
            }
        }

        return [];
    }

    private static GenerateFieldIds GetFieldIdsOptionFromType(INamedTypeSymbol typeSymbol, LibraryTypes libraryTypes)
    {
        var attribute = typeSymbol.GetAttribute(libraryTypes.GenerateSerializerAttribute);
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

    private static IEnumerable<IMemberDescription> CollectDataMembers(FieldIdAssignmentHelper fieldIdAssignmentHelper)
    {
        var members = new Dictionary<(uint, bool), IMemberDescription>();

        foreach (var member in fieldIdAssignmentHelper.Members)
        {
            if (!fieldIdAssignmentHelper.TryGetSymbolKey(member, out var key))
            {
                continue;
            }

            var (id, isConstructorParameter) = key;

            if (member is IPropertySymbol property && !members.ContainsKey((id, isConstructorParameter)))
            {
                members[(id, isConstructorParameter)] = new PropertyDescription(id, isConstructorParameter, property);
            }

            if (member is IFieldSymbol field)
            {
                if (!members.TryGetValue((id, isConstructorParameter), out var existing) || existing is IPropertyDescription)
                {
                    members[(id, isConstructorParameter)] = new FieldDescription(id, isConstructorParameter, field);
                }
            }
        }

        return members.Values;
    }
}


