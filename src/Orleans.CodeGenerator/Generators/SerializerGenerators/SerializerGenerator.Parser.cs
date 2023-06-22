namespace Orleans.CodeGenerator.Generators.SerializerGenerators;

using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Orleans.CodeGenerator.Diagnostics;
using Orleans.CodeGenerator.Hashing;
using Orleans.CodeGenerator.SyntaxGeneration;

internal partial class SerializerGenerator
{
    internal class Parser : ParserBase
    {

        private static SerializerGeneratorContext _serializerContext;
        private static ParserSpecs _parserSpecs;
        private CodeGeneratorOptions _options;
        private static LibraryTypes _libraryTypes;
        private readonly INamedTypeSymbol[] _generateSerializerAttributes;
        private static INamedTypeSymbol _fSharpSourceConstructFlagsOrDefault;
        private static INamedTypeSymbol _fSharpCompilationMappingAttributeOrDefault;

        internal LibraryTypes LibraryTypes { get => _libraryTypes; set => _libraryTypes = value; }

        public Parser(ParserSpecs parserSpecs) : base(parserSpecs.Compilation)
        {
            //Debugger.Launch();
            _parserSpecs = parserSpecs;
            _options = parserSpecs.CodeGeneratorOptions;
            LibraryTypes = LibraryTypes.FromCompilation(parserSpecs.Compilation, parserSpecs.CodeGeneratorOptions);
            _generateSerializerAttributes = parserSpecs.CodeGeneratorOptions.GenerateSerializerAttributes.Select(compilation.GetTypeByMetadataName).ToArray();

            _serializerContext = new()
            {
                LibraryTypes = _libraryTypes,
                AssemblyName = compilation.AssemblyName
            };
        }


        public override IncrementalGeneratorContext Parse(CancellationToken token)
        {
            SetContextValuesForCurrentAssemblies(token);
            SetContextValuesForDeclaringAssemblies(token);
            return _serializerContext;
        }

        private void SetContextValuesForDeclaringAssemblies(CancellationToken token)
        {
            var declaringAssemblies = GetDeclaringAssemblies();
            if (!declaringAssemblies.Any()) return;

            _fSharpSourceConstructFlagsOrDefault ??= TypeOrDefault("Microsoft.FSharp.Core.SourceConstructFlags");
            _fSharpCompilationMappingAttributeOrDefault ??= TypeOrDefault("Microsoft.FSharp.Core.CompilationMappingAttribute");
            var proxyBaseTypeInvokableBaseTypes = new Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, INamedTypeSymbol>>(SymbolEqualityComparer.Default);


            foreach (var assembly in declaringAssemblies)
            {
                foreach (var symbol in assembly.GetDeclaredTypes())
                {
                    var syntaxTree = symbol.DeclaringSyntaxReferences.FirstOrDefault()?.SyntaxTree ?? compilation.SyntaxTrees.First();
                    var semanticModel = compilation.GetSemanticModel(syntaxTree);
                    if (FSharpUtilities.IsUnionCase(LibraryTypes, symbol, out var sumType) && ShouldGenerateSerializer(sumType))
                    {
                        if (!semanticModel.IsAccessible(0, sumType))
                        {
                            throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(sumType));
                        }

                        var typeDescription = new FSharpUtilities.FSharpUnionCaseTypeDescription(semanticModel, symbol, LibraryTypes);
                        _serializerContext.SerializableTypes.Add(typeDescription);
                    }
                    else if (ShouldGenerateSerializer(symbol))
                    {
                        if (!semanticModel.IsAccessible(0, symbol))
                        {
                            throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(symbol));
                        }

                        if (FSharpUtilities.IsRecord(LibraryTypes, symbol))
                        {
                            var typeDescription = new FSharpUtilities.FSharpRecordTypeDescription(semanticModel, symbol, LibraryTypes);
                            _serializerContext.SerializableTypes.Add(typeDescription);
                        }
                        else
                        {
                            // Regular type
                            var includePrimaryConstructorParameters = IncludePrimaryConstructorParameters(symbol);
                            var constructorParameters = ImmutableArray<IParameterSymbol>.Empty;
                            if (includePrimaryConstructorParameters)
                            {
                                if (symbol.IsRecord)
                                {
                                    // If there is a primary constructor then that will be declared before the copy constructor
                                    // A record always generates a copy constructor and marks it as implicitly declared
                                    // todo: find an alternative to this magic
                                    var potentialPrimaryConstructor = symbol.Constructors[0];
                                    if (!potentialPrimaryConstructor.IsImplicitlyDeclared)
                                    {
                                        constructorParameters = potentialPrimaryConstructor.Parameters;
                                    }
                                }
                                else
                                {
                                    var annotatedConstructors = symbol.Constructors.Where(ctor => ctor.HasAnyAttribute(LibraryTypes.ConstructorAttributeTypes)).ToList();
                                    if (annotatedConstructors.Count == 1)
                                    {
                                        constructorParameters = annotatedConstructors[0].Parameters;
                                    }
                                }
                            }

                            var implicitMemberSelectionStrategy = (_options.GenerateFieldIds, GetGenerateFieldIdsOptionFromType(symbol)) switch
                            {
                                (_, GenerateFieldIds.PublicProperties) => GenerateFieldIds.PublicProperties,
                                (GenerateFieldIds.PublicProperties, _) => GenerateFieldIds.PublicProperties,
                                _ => GenerateFieldIds.None
                            };
                            var fieldIdAssignmentHelper = new FieldIdAssignmentHelper(symbol, constructorParameters, implicitMemberSelectionStrategy, LibraryTypes);
                            if (!fieldIdAssignmentHelper.IsValidForSerialization)
                            {
                                throw new OrleansGeneratorDiagnosticAnalysisException(CanNotGenerateImplicitFieldIdsDiagnostic.CreateDiagnostic(symbol, fieldIdAssignmentHelper.FailureReason));
                            }

                            var typeDescription = new SerializableTypeDescription(semanticModel, symbol, includePrimaryConstructorParameters, GetDataMembers(fieldIdAssignmentHelper), LibraryTypes);
                            _serializerContext.SerializableTypes.Add(typeDescription);
                        }
                    }

                    if (symbol.TypeKind == TypeKind.Interface)
                    {
                        var attribute = HasAttribute(
                            symbol, LibraryTypes.GenerateMethodSerializersAttribute,
                            inherited: true);
                        if (attribute != null)
                        {
                            var prop = symbol.GetAllMembers<IPropertySymbol>().FirstOrDefault();
                            if (prop is { })
                            {
                                throw new OrleansGeneratorDiagnosticAnalysisException(RpcInterfacePropertyDiagnostic.CreateDiagnostic(symbol, prop));
                            }

                            var baseClass = (INamedTypeSymbol)attribute.ConstructorArguments[0].Value;
                            var isExtension = (bool)attribute.ConstructorArguments[1].Value;
                            var invokableBaseTypes = GetInvokableBaseTypes(proxyBaseTypeInvokableBaseTypes, baseClass);

                            var description = new InvokableInterfaceDescription(
                                LibraryTypes,
                                semanticModel,
                                symbol,
                                GetAlias(symbol) ?? symbol.Name,
                                baseClass,
                                isExtension,
                                invokableBaseTypes);
                            _serializerContext.InvokableInterfaces.Add(description);
                        }
                    }

                    if ((symbol.TypeKind == TypeKind.Class || symbol.TypeKind == TypeKind.Struct) && !symbol.IsAbstract && (symbol.DeclaredAccessibility == Accessibility.Public || symbol.DeclaredAccessibility == Accessibility.Internal))
                    {
                        if (symbol.HasAttribute(LibraryTypes.RegisterSerializerAttribute))
                        {
                            _serializerContext.DetectedSerializers.Add(symbol);
                        }

                        if (symbol.HasAttribute(LibraryTypes.RegisterActivatorAttribute))
                        {
                            _serializerContext.DetectedActivators.Add(symbol);
                        }

                        if (symbol.HasAttribute(LibraryTypes.RegisterCopierAttribute))
                        {
                            _serializerContext.DetectedCopiers.Add(symbol);
                        }

                        if (symbol.HasAttribute(LibraryTypes.RegisterConverterAttribute))
                        {
                            _serializerContext.DetectedConverters.Add(symbol);
                        }

                        // Find all implementations of invokable interfaces
                        foreach (var iface in symbol.AllInterfaces)
                        {
                            var attribute = HasAttribute(
                                iface,
                                LibraryTypes.GenerateMethodSerializersAttribute,
                                inherited: true);
                            if (attribute != null)
                            {
                                _serializerContext.InvokableInterfaceImplementations.Add(symbol);
                                break;
                            }
                        }
                    }
                }
            }


            bool ShouldGenerateSerializer(INamedTypeSymbol t)
            {
                if (t.HasAttribute(LibraryTypes.GenerateSerializerAttribute))
                {
                    return true;
                }

                foreach (var attr in _generateSerializerAttributes)
                {
                    if (HasAttribute(t, attr, inherited: true) != null)
                    {
                        return true;
                    }
                }

                return false;
            }



        }




        IncrementalGeneratorContext SetContextValuesForCurrentAssemblies(CancellationToken token)
        {
            SetContextValuesForGenerateSerializers();
            SetContextValuesForInterfaceTypes();
            SetContextValuesForConcreateTypes();
            return _serializerContext;
        }

        private void SetContextValuesForConcreateTypes()
        {
            foreach (var item in _parserSpecs.RegisterSerializers)
            {
                var symbol = item.Item2.GetDeclaredSymbol(item.Item1);
                var semanticModel = item.Item2;

                if (symbol.DeclaredAccessibility is not Accessibility.Public or Accessibility.Internal) continue;

                _serializerContext.DetectedSerializers.Add(symbol);

                AddInvokableInterfaceImplementations(symbol);
            }


            foreach (var item in _parserSpecs.RegisterActivators)
            {
                var symbol = item.Item2.GetDeclaredSymbol(item.Item1);
                var semanticModel = item.Item2;

                if (symbol.DeclaredAccessibility is not Accessibility.Public or Accessibility.Internal) continue;

                _serializerContext.DetectedActivators.Add(symbol);

                AddInvokableInterfaceImplementations(symbol);
            }

            foreach (var item in _parserSpecs.RegisterCopiers)
            {
                var symbol = item.Item2.GetDeclaredSymbol(item.Item1);
                var semanticModel = item.Item2;

                if (symbol.DeclaredAccessibility is not Accessibility.Public or Accessibility.Internal) continue;

                _serializerContext.DetectedCopiers.Add(symbol);

                AddInvokableInterfaceImplementations(symbol);
            }

            foreach (var item in _parserSpecs.RegisterConverters)
            {
                var symbol = item.Item2.GetDeclaredSymbol(item.Item1);
                var semanticModel = item.Item2;

                if (symbol.DeclaredAccessibility is not Accessibility.Public or Accessibility.Internal) continue;

                _serializerContext.DetectedConverters.Add(symbol);

                AddInvokableInterfaceImplementations(symbol);
            }


        }

        private void AddInvokableInterfaceImplementations(INamedTypeSymbol symbol)
        {
            // Find all implementations of invokable interfaces
            foreach (var iface in symbol.AllInterfaces)
            {
                var attribute = HasAttribute(
                    iface,
                    LibraryTypes.GenerateMethodSerializersAttribute,
                    inherited: true);
                if (attribute != null)
                {
                    _serializerContext.InvokableInterfaceImplementations.Add(symbol);
                    break;
                }
            }
        }

        private void SetContextValuesForInterfaceTypes()
        {
            var proxyBaseTypeInvokableBaseTypes = new Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, INamedTypeSymbol>>(SymbolEqualityComparer.Default);

            foreach (var item in _parserSpecs.SerializerInterfaces)
            {
                var symbol = item.Item2.GetDeclaredSymbol(item.Item1);
                var semanticModel = item.Item2;
                var attribute = HasAttribute(
                                                        symbol,
                                                        LibraryTypes.GenerateMethodSerializersAttribute,
                                                        inherited: true);
                if (attribute != null)
                {
                    var prop = symbol.GetAllMembers<IPropertySymbol>().FirstOrDefault();
                    if (prop is { })
                    {
                        throw new OrleansGeneratorDiagnosticAnalysisException(RpcInterfacePropertyDiagnostic.CreateDiagnostic(symbol, prop));
                    }

                    var baseClass = (INamedTypeSymbol)attribute.ConstructorArguments[0].Value;
                    var isExtension = (bool)attribute.ConstructorArguments[1].Value;
                    var invokableBaseTypes = GetInvokableBaseTypes(proxyBaseTypeInvokableBaseTypes, baseClass);

                    var description = new InvokableInterfaceDescription(
                        _libraryTypes,
                        semanticModel,
                        symbol,
                        GetAlias(symbol) ?? symbol.Name,
                        baseClass,
                        isExtension,
                        invokableBaseTypes);
                    _serializerContext.InvokableInterfaces.Add(description);
                }
            }

        }

        private void SetContextValuesForGenerateSerializers()
        {
            foreach (var item in _parserSpecs.GenerateSerializers)
            {
                var symbol = item.Item2.GetDeclaredSymbol(item.Item1);
                var semanticModel = item.Item2;
                if (FSharpUtilities.IsUnionCase(LibraryTypes, symbol, out var sumType))
                {
                    if (!semanticModel.IsAccessible(0, sumType))
                    {
                        throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(sumType));
                    }

                    var typeDescription = new FSharpUtilities.FSharpUnionCaseTypeDescription(semanticModel, symbol, LibraryTypes);
                    _serializerContext.SerializableTypes.Add(typeDescription);
                }
                else
                {
                    if (!semanticModel.IsAccessible(0, symbol))
                    {
                        throw new OrleansGeneratorDiagnosticAnalysisException(InaccessibleSerializableTypeDiagnostic.CreateDiagnostic(symbol));
                    }

                    if (FSharpUtilities.IsRecord(LibraryTypes, symbol))
                    {
                        var typeDescription = new FSharpUtilities.FSharpRecordTypeDescription(semanticModel, symbol, LibraryTypes);
                        _serializerContext.SerializableTypes.Add(typeDescription);
                    }
                    else
                    {
                        // Regular type
                        var includePrimaryConstructorParameters = IncludePrimaryConstructorParameters(symbol);
                        var constructorParameters = ImmutableArray<IParameterSymbol>.Empty;
                        if (includePrimaryConstructorParameters)
                        {
                            if (symbol.IsRecord)
                            {
                                // If there is a primary constructor then that will be declared before the copy constructor
                                // A record always generates a copy constructor and marks it as implicitly declared
                                // todo: find an alternative to this magic
                                var potentialPrimaryConstructor = symbol.Constructors[0];
                                if (!potentialPrimaryConstructor.IsImplicitlyDeclared)
                                {
                                    constructorParameters = potentialPrimaryConstructor.Parameters;
                                }
                            }
                            else
                            {
                                var annotatedConstructors = symbol.Constructors.Where(ctor => ctor.HasAnyAttribute(LibraryTypes.ConstructorAttributeTypes)).ToList();
                                if (annotatedConstructors.Count == 1)
                                {
                                    constructorParameters = annotatedConstructors[0].Parameters;
                                }
                            }
                        }

                        var implicitMemberSelectionStrategy = (_options.GenerateFieldIds, GetGenerateFieldIdsOptionFromType(symbol)) switch
                        {
                            (_, GenerateFieldIds.PublicProperties) => GenerateFieldIds.PublicProperties,
                            (GenerateFieldIds.PublicProperties, _) => GenerateFieldIds.PublicProperties,
                            _ => GenerateFieldIds.None
                        };
                        var fieldIdAssignmentHelper = new FieldIdAssignmentHelper(symbol, constructorParameters, implicitMemberSelectionStrategy, LibraryTypes);
                        if (!fieldIdAssignmentHelper.IsValidForSerialization)
                        {
                            throw new OrleansGeneratorDiagnosticAnalysisException(CanNotGenerateImplicitFieldIdsDiagnostic.CreateDiagnostic(symbol, fieldIdAssignmentHelper.FailureReason));
                        }

                        var typeDescription = new SerializableTypeDescription(semanticModel, symbol, includePrimaryConstructorParameters, GetDataMembers(fieldIdAssignmentHelper), LibraryTypes);
                        _serializerContext.SerializableTypes.Add(typeDescription);
                    }
                }
            }
        }

        internal string GetAlias(ISymbol symbol)
        {
            return GetAlias(_libraryTypes, symbol);
        }

        internal static string GetAlias(LibraryTypes libraryTypes, ISymbol symbol)
        {
            return (string)symbol.GetAttribute(libraryTypes.AliasAttribute)?.ConstructorArguments.First().Value;
        }


        internal uint? GetId(ISymbol memberSymbol) => GetId(LibraryTypes, memberSymbol);

        internal static uint? GetId(LibraryTypes libraryTypes, ISymbol memberSymbol)
        {
            return memberSymbol.GetAnyAttribute(libraryTypes.IdAttributeTypes) is { } attr
                ? (uint)attr.ConstructorArguments.First().Value
                : null;
        }


        private GenerateFieldIds GetGenerateFieldIdsOptionFromType(INamedTypeSymbol t)
        {
            var attribute = t.GetAttribute(LibraryTypes.GenerateSerializerAttribute);
            if (attribute == null)
                return GenerateFieldIds.None;

            foreach (var namedArgument in attribute.NamedArguments)
            {
                if (namedArgument.Key == "GenerateFieldIds")
                {
                    var value = namedArgument.Value.Value;
                    return value == null ? GenerateFieldIds.None : (GenerateFieldIds)(int)value;
                }
            }
            return GenerateFieldIds.None;
        }

        private bool IncludePrimaryConstructorParameters(INamedTypeSymbol t)
        {
            static bool? TestGenerateSerializerAttribute(INamedTypeSymbol t, INamedTypeSymbol at)
            {
                var attribute = t.GetAttribute(at);
                if (attribute != null)
                {
                    foreach (var namedArgument in attribute.NamedArguments)
                    {
                        if (namedArgument.Key == "IncludePrimaryConstructorParameters")
                        {
                            if (namedArgument.Value.Kind == TypedConstantKind.Primitive && namedArgument.Value.Value is bool b)
                            {
                                return b;
                            }
                        }
                    }
                }

                // If there is no such named argument, return null so that other attributes have a chance to apply and defaults can be applied.
                return null;
            }

            if (TestGenerateSerializerAttribute(t, LibraryTypes.GenerateSerializerAttribute) is bool result)
            {
                return result;
            }

            foreach (var attr in _generateSerializerAttributes)
            {
                if (TestGenerateSerializerAttribute(t, attr) is bool res)
                {
                    return res;
                }
            }


            // Default to true for records, false otherwise.
            return t.IsRecord;
        }



        private Dictionary<INamedTypeSymbol, INamedTypeSymbol> GetInvokableBaseTypes(Dictionary<INamedTypeSymbol, Dictionary<INamedTypeSymbol, INamedTypeSymbol>> proxyBaseTypeInvokableBaseTypes, INamedTypeSymbol baseClass)
        {
            // Set the base invokable types which are used if attributes on individual methods do not override them.
            if (!proxyBaseTypeInvokableBaseTypes.TryGetValue(baseClass, out var invokableBaseTypes))
            {
                invokableBaseTypes = new Dictionary<INamedTypeSymbol, INamedTypeSymbol>(SymbolEqualityComparer.Default);
                if (baseClass.GetAttributes(LibraryTypes.DefaultInvokableBaseTypeAttribute, out var invokableBaseTypeAttributes))
                {
                    foreach (var attr in invokableBaseTypeAttributes)
                    {
                        var ctorArgs = attr.ConstructorArguments;
                        var returnType = (INamedTypeSymbol)ctorArgs[0].Value;
                        var invokableBaseType = (INamedTypeSymbol)ctorArgs[1].Value;
                        invokableBaseTypes[returnType] = invokableBaseType;
                    }
                }

                proxyBaseTypeInvokableBaseTypes[baseClass] = invokableBaseTypes;
            }

            return invokableBaseTypes;
        }

        private IEnumerable<IMemberDescription> GetDataMembers(FieldIdAssignmentHelper fieldIdAssignmentHelper)
        {
            var members = new Dictionary<(uint, bool), IMemberDescription>();

            foreach (var member in fieldIdAssignmentHelper.Members)
            {
                if (!fieldIdAssignmentHelper.TryGetSymbolKey(member, out var key))
                    continue;
                var (id, isConstructorParameter) = key;

                // FieldDescription takes precedence over PropertyDescription (never replace)
                if (member is IPropertySymbol property && !members.TryGetValue((id, isConstructorParameter), out _))
                {
                    members[(id, isConstructorParameter)] = new PropertyDescription(id, isConstructorParameter, property);
                }

                if (member is IFieldSymbol field)
                {
                    // FieldDescription takes precedence over PropertyDescription (add or replace)
                    if (!members.TryGetValue((id, isConstructorParameter), out var existing) || existing is PropertyDescription)
                    {
                        members[(id, isConstructorParameter)] = new FieldDescription(id, isConstructorParameter, field);
                    }
                }
            }
            return members.Values;
        }


        internal static string CreateHashedMethodId(IMethodSymbol methodSymbol)
        {
            var methodSignature = Format(methodSymbol);
            var hash = XxHash32.Hash(Encoding.UTF8.GetBytes(methodSignature));
            return $"{HexConverter.ToString(hash)}";

            static string Format(IMethodSymbol methodInfo)
            {
                var result = new StringBuilder();
                result.Append(methodInfo.ContainingType.ToDisplayName());
                result.Append('.');
                result.Append(methodInfo.Name);

                if (methodInfo.IsGenericMethod)
                {
                    result.Append('<');
                    var first = true;
                    foreach (var typeArgument in methodInfo.TypeArguments)
                    {
                        if (!first) result.Append(',');
                        else first = false;
                        result.Append(typeArgument.Name);
                    }

                    result.Append('>');
                }

                {
                    result.Append('(');
                    var parameters = methodInfo.Parameters;
                    var first = true;
                    foreach (var parameter in parameters)
                    {
                        if (!first)
                        {
                            result.Append(',');
                        }

                        var parameterType = parameter.Type;
                        switch (parameterType)
                        {
                            case ITypeParameterSymbol _:
                                result.Append(parameterType.Name);
                                break;
                            default:
                                result.Append(parameterType.ToDisplayName());
                                break;
                        }

                        first = false;
                    }
                }

                result.Append(')');
                return result.ToString();
            }
        }
    }
}
