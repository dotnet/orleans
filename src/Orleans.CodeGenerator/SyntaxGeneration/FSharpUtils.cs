using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Orleans.CodeGenerator.SerializerGenerator;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    internal static class FSharpUtilities
    {
        private const int SourceConstructFlagsSumTypeValue = 1;
        private const int SourceConstructFlagsRecordTypeValue = 2;

        public static bool IsUnionCase(LibraryTypes libraryTypes, INamedTypeSymbol symbol, out INamedTypeSymbol sumType)
        {
            sumType = default;
            var compilationAttributeType = libraryTypes.FSharpCompilationMappingAttributeOrDefault;
            var sourceConstructFlagsType = libraryTypes.FSharpSourceConstructFlagsOrDefault;
            var baseType = symbol.BaseType;
            if (compilationAttributeType is null || sourceConstructFlagsType is null || baseType is null)
            {
                return false;
            }

            if (!baseType.GetAttributes(compilationAttributeType, out var compilationAttributes) || compilationAttributes.Length == 0)
            {
                return false;
            }

            var compilationAttribute = compilationAttributes[0];
            var foundArg = false;
            TypedConstant sourceConstructFlagsArgument = default;
            foreach (var arg in compilationAttribute.ConstructorArguments)
            {
                if (SymbolEqualityComparer.Default.Equals(arg.Type, sourceConstructFlagsType))
                {
                    sourceConstructFlagsArgument = arg;
                    foundArg = true;
                    break;
                }
            }

            if (!foundArg)
            {
                return false;
            }

            if ((int)sourceConstructFlagsArgument.Value != SourceConstructFlagsSumTypeValue)
            {
                return false;
            }

            sumType = baseType;
            return true;
        }

        public static bool IsRecord(LibraryTypes libraryTypes, INamedTypeSymbol symbol)
        {
            var compilationAttributeType = libraryTypes.FSharpCompilationMappingAttributeOrDefault;
            var sourceConstructFlagsType = libraryTypes.FSharpSourceConstructFlagsOrDefault;
            if (compilationAttributeType is null || sourceConstructFlagsType is null)
            {
                return false;
            }

            if (!symbol.GetAttributes(compilationAttributeType, out var compilationAttributes) || compilationAttributes.Length == 0)
            {
                return false;
            }

            var compilationAttribute = compilationAttributes[0];
            var foundArg = false;
            TypedConstant sourceConstructFlagsArgument = default;
            foreach (var arg in compilationAttribute.ConstructorArguments)
            {
                if (SymbolEqualityComparer.Default.Equals(arg.Type, sourceConstructFlagsType))
                {
                    sourceConstructFlagsArgument = arg;
                    foundArg = true;
                    break;
                }
            }

            if (!foundArg)
            {
                return false;
            }

            if ((int)sourceConstructFlagsArgument.Value != SourceConstructFlagsRecordTypeValue)
            {
                return false;
            }

            return true;
        }

        public class FSharpUnionCaseTypeDescription : SerializableTypeDescription
        {
            public FSharpUnionCaseTypeDescription(SemanticModel semanticModel, INamedTypeSymbol type, LibraryTypes libraryTypes) : base(semanticModel, type, GetUnionCaseDataMembers(libraryTypes, type), libraryTypes)
            {
            }

            private static IEnumerable<IMemberDescription> GetUnionCaseDataMembers(LibraryTypes libraryTypes, INamedTypeSymbol symbol)
            {
                List<IPropertySymbol> dataMembers = new();
                foreach (var property in symbol.GetDeclaredInstanceMembers<IPropertySymbol>())
                {
                    if (!property.Name.StartsWith("Item", System.StringComparison.Ordinal))
                    {
                        continue;
                    }

                    dataMembers.Add(property);
                }

                dataMembers.Sort(FSharpUnionCasePropertyNameComparer.Default);

                ushort id = 0;
                foreach (var field in dataMembers)
                {
                    yield return new FSharpUnionCaseFieldDescription(libraryTypes, field, id);
                    id++;
                }
            }

            private class FSharpUnionCasePropertyNameComparer : IComparer<IPropertySymbol>
            {
                public static FSharpUnionCasePropertyNameComparer Default { get; } = new FSharpUnionCasePropertyNameComparer();

                public int Compare(IPropertySymbol x, IPropertySymbol y)
                {
                    var xName = x.Name;
                    var yName = y.Name;
                    if (xName.Length > yName.Length)
                    {
                        return 1;
                    }

                    if (xName.Length < yName.Length)
                    {
                        return -1;
                    }

                    return string.CompareOrdinal(xName, yName);
                }
            }

            private class FSharpUnionCaseFieldDescription : IMemberDescription, ISerializableMember
            {
                private readonly LibraryTypes _libraryTypes;
                private readonly IPropertySymbol _property;

                public FSharpUnionCaseFieldDescription(LibraryTypes libraryTypes, IPropertySymbol property, ushort ordinal)
                {
                    _libraryTypes = libraryTypes;
                    FieldId = ordinal;
                    _property = property;
                }

                public ushort FieldId { get; }

                public bool IsShallowCopyable => _libraryTypes.IsShallowCopyable(Type) || _property.HasAnyAttribute(_libraryTypes.ImmutableAttributes);

                public bool IsValueType => Type.IsValueType;

                public IMemberDescription Member => this;

                public ITypeSymbol Type => _property.Type;

                public INamedTypeSymbol ContainingType => _property.ContainingType;

                public ISymbol Symbol => _property;

                public string FieldName => _property.Name.ToLowerInvariant(); 

                /// <summary>
                /// Gets the name of the setter field.
                /// </summary>
                private string SetterFieldName => "setField" + FieldId;

                /// <summary>
                /// Gets syntax representing the type of this field.
                /// </summary>
                public TypeSyntax TypeSyntax => Type.TypeKind == TypeKind.Dynamic
                    ? PredefinedType(Token(SyntaxKind.ObjectKeyword)) 
                    : GetTypeSyntax(Type);

                /// <summary>
                /// Gets the <see cref="Property"/> which this field is the backing property for, or
                /// <see langword="null" /> if this is not the backing field of an auto-property.
                /// </summary>
                private IPropertySymbol Property => _property;

                public string AssemblyName => Type.ContainingAssembly.ToDisplayName();
                public string TypeName => Type.ToDisplayName();
                public string TypeNameIdentifier => Type.GetValidIdentifier();

                public TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol) => typeSymbol.ToTypeSyntax();

                /// <summary>
                /// Returns syntax for retrieving the value of this field, deep copying it if necessary.
                /// </summary>
                /// <param name="instance">The instance of the containing type.</param>
                /// <returns>Syntax for retrieving the value of this field.</returns>
                public ExpressionSyntax GetGetter(ExpressionSyntax instance) => instance.Member(Property.Name);

                /// <summary>
                /// Returns syntax for setting the value of this field.
                /// </summary>
                /// <param name="instance">The instance of the containing type.</param>
                /// <param name="value">Syntax for the new value.</param>
                /// <returns>Syntax for setting the value of this field.</returns>
                public ExpressionSyntax GetSetter(ExpressionSyntax instance, ExpressionSyntax value)
                {
                    var instanceArg = Argument(instance);
                    if (ContainingType != null && ContainingType.IsValueType)
                    {
                        instanceArg = instanceArg.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                    }

                    return
                        InvocationExpression(IdentifierName(SetterFieldName))
                            .AddArgumentListArguments(instanceArg, Argument(value));
                }

                public GetterFieldDescription GetGetterFieldDescription() => null;

                public SetterFieldDescription GetSetterFieldDescription()
                {
                    TypeSyntax fieldType;
                    if (ContainingType != null && ContainingType.IsValueType)
                    {
                        fieldType = _libraryTypes.ValueTypeSetter_2.ToTypeSyntax(GetTypeSyntax(ContainingType), TypeSyntax);
                    }
                    else
                    {
                        fieldType = _libraryTypes.Action_2.ToTypeSyntax(GetTypeSyntax(ContainingType), TypeSyntax);
                    }

                    // Generate syntax to initialize the field in the constructor
                    var fieldAccessorUtility = AliasQualifiedName("global", IdentifierName("Orleans.Serialization")).Member("Utilities").Member("FieldAccessor");
                    var fieldInfo = SerializableMember.GetGetFieldInfoExpression(ContainingType, FieldName);
                    var isContainedByValueType = ContainingType != null && ContainingType.IsValueType;
                    var accessorMethod = isContainedByValueType ? "GetValueSetter" : "GetReferenceSetter";
                    var accessorInvoke = CastExpression(
                        fieldType,
                        InvocationExpression(fieldAccessorUtility.Member(accessorMethod))
                            .AddArgumentListArguments(Argument(fieldInfo)));

                    var initializationSyntax = ExpressionStatement(
                        AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(SetterFieldName), accessorInvoke));

                    return new SetterFieldDescription(fieldType, SetterFieldName, initializationSyntax);
                }
            }
        }

        public class FSharpRecordTypeDescription : SerializableTypeDescription
        {
            public FSharpRecordTypeDescription(SemanticModel semanticModel, INamedTypeSymbol type, LibraryTypes libraryTypes) : base(semanticModel, type, GetRecordDataMembers(libraryTypes, type), libraryTypes)
            {
            }

            private static IEnumerable<IMemberDescription> GetRecordDataMembers(LibraryTypes libraryTypes, INamedTypeSymbol symbol)
            {
                List<(IPropertySymbol, ushort)> dataMembers = new();
                foreach (var property in symbol.GetDeclaredInstanceMembers<IPropertySymbol>())
                {
                    var id = CodeGenerator.GetId(libraryTypes, property);
                    if (!id.HasValue)
                    {
                        continue;
                    }

                    dataMembers.Add((property, id.Value));
                }

                foreach (var (property, id) in dataMembers)
                {
                    yield return new FSharpRecordPropertyDescription(libraryTypes, property, id);
                }
            }

            private class FSharpRecordPropertyDescription : IMemberDescription, ISerializableMember
            {
                private readonly LibraryTypes _libraryTypes;
                private readonly IPropertySymbol _property;

                public FSharpRecordPropertyDescription(LibraryTypes libraryTypes, IPropertySymbol property, ushort ordinal)
                {
                    _libraryTypes = libraryTypes;
                    FieldId = ordinal;
                    _property = property;
                }

                public ushort FieldId { get; }

                public bool IsShallowCopyable => _libraryTypes.IsShallowCopyable(Type) || _property.HasAnyAttribute(_libraryTypes.ImmutableAttributes);

                public bool IsValueType => Type.IsValueType;

                public IMemberDescription Member => this;

                public ITypeSymbol Type => _property.Type;

                public ISymbol Symbol => _property;

                public INamedTypeSymbol ContainingType => _property.ContainingType;

                public string FieldName => _property.Name + "@"; 

                /// <summary>
                /// Gets the name of the setter field.
                /// </summary>
                private string SetterFieldName => "setField" + FieldId;

                /// <summary>
                /// Gets syntax representing the type of this field.
                /// </summary>
                public TypeSyntax TypeSyntax => Type.TypeKind == TypeKind.Dynamic
                    ? PredefinedType(Token(SyntaxKind.ObjectKeyword)) 
                    : GetTypeSyntax(Type);

                /// <summary>
                /// Gets the <see cref="Property"/> which this field is the backing property for, or
                /// <see langword="null" /> if this is not the backing field of an auto-property.
                /// </summary>
                private IPropertySymbol Property => _property;

                public string AssemblyName => Type.ContainingAssembly.ToDisplayName();
                public string TypeName => Type.ToDisplayName();
                public string TypeNameIdentifier => Type.GetValidIdentifier();

                public TypeSyntax GetTypeSyntax(ITypeSymbol typeSymbol) => typeSymbol.ToTypeSyntax();

                /// <summary>
                /// Returns syntax for retrieving the value of this field, deep copying it if necessary.
                /// </summary>
                /// <param name="instance">The instance of the containing type.</param>
                /// <returns>Syntax for retrieving the value of this field.</returns>
                public ExpressionSyntax GetGetter(ExpressionSyntax instance) => instance.Member(Property.Name);

                /// <summary>
                /// Returns syntax for setting the value of this field.
                /// </summary>
                /// <param name="instance">The instance of the containing type.</param>
                /// <param name="value">Syntax for the new value.</param>
                /// <returns>Syntax for setting the value of this field.</returns>
                public ExpressionSyntax GetSetter(ExpressionSyntax instance, ExpressionSyntax value)
                {
                    var instanceArg = Argument(instance);
                    if (ContainingType != null && ContainingType.IsValueType)
                    {
                        instanceArg = instanceArg.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                    }

                    return
                        InvocationExpression(IdentifierName(SetterFieldName))
                            .AddArgumentListArguments(instanceArg, Argument(value));
                }

                public GetterFieldDescription GetGetterFieldDescription() => null;

                public SetterFieldDescription GetSetterFieldDescription()
                {
                    TypeSyntax fieldType;
                    if (ContainingType != null && ContainingType.IsValueType)
                    {
                        fieldType = _libraryTypes.ValueTypeSetter_2.ToTypeSyntax(GetTypeSyntax(ContainingType), TypeSyntax);
                    }
                    else
                    {
                        fieldType = _libraryTypes.Action_2.ToTypeSyntax(GetTypeSyntax(ContainingType), TypeSyntax);
                    }

                    // Generate syntax to initialize the field in the constructor
                    var fieldAccessorUtility = AliasQualifiedName("global", IdentifierName("Orleans.Serialization")).Member("Utilities").Member("FieldAccessor");
                    var fieldInfo = SerializableMember.GetGetFieldInfoExpression(ContainingType, FieldName);
                    var isContainedByValueType = ContainingType != null && ContainingType.IsValueType;
                    var accessorMethod = isContainedByValueType ? "GetValueSetter" : "GetReferenceSetter";
                    var accessorInvoke = CastExpression(
                        fieldType,
                        InvocationExpression(fieldAccessorUtility.Member(accessorMethod))
                            .AddArgumentListArguments(Argument(fieldInfo)));

                    var initializationSyntax = ExpressionStatement(
                        AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(SetterFieldName), accessorInvoke));

                    return new SetterFieldDescription(fieldType, SetterFieldName, initializationSyntax);
                }
            }
        }
    }
}
