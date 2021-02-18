using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Analyzers;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
using ITypeSymbol = Microsoft.CodeAnalysis.ITypeSymbol;

namespace Orleans.CodeGenerator.Generators
{
    /// <summary>
    /// Code generator which generates serializers.
    /// Sample of generated serializer:
    /// [global::System.CodeDom.Compiler.GeneratedCodeAttribute("Orleans-CodeGenerator", "2.0.0.0"), global::System.Diagnostics.CodeAnalysis.ExcludeFromCodeCoverageAttribute, global::Orleans.CodeGeneration.SerializerAttribute(typeof(global::MyType))]
    /// internal sealed class OrleansCodeGenUnitTests_GrainInterfaces_MyTypeSerializer
    /// {
    ///    private readonly global::System.Func&lt;global::MyType, global::System.Int32&gt; getField0;
    ///    private readonly global::System.Action&lt;global::MyType, global::System.Int32&gt; setField0;
    ///    public OrleansCodeGenUnitTests_GrainInterfaces_MyTypeSerializer(global::Orleans.Serialization.IFieldUtils fieldUtils)
    ///    {
    ///        [...]
    ///    }
    ///    [global::Orleans.CodeGeneration.CopierMethodAttribute]
    ///    public global::System.Object DeepCopier(global::System.Object original, global::Orleans.Serialization.ICopyContext context)
    ///    {
    ///            [...]
    ///    }
    ///    [global::Orleans.CodeGeneration.SerializerMethodAttribute]
    ///    public void Serializer(global::System.Object untypedInput, global::Orleans.Serialization.ISerializationContext context, global::System.Type expected)
    ///    {
    ///            [...]
    ///    }
    ///    [global::Orleans.CodeGeneration.DeserializerMethodAttribute]
    ///    public global::System.Object Deserializer(global::System.Type expected, global::Orleans.Serialization.IDeserializationContext context)
    ///    {
    ///            [...]
    ///    }
    ///}
    /// </summary>
    internal class SerializerGenerator
    {
        /// <summary>
        /// The suffix appended to the name of generated classes.
        /// </summary>
        private const string ClassSuffix = "Serializer";
        private readonly CodeGeneratorOptions options;
        private readonly WellKnownTypes wellKnownTypes;

        public SerializerGenerator(CodeGeneratorOptions options, WellKnownTypes wellKnownTypes)
        {
            this.options = options;
            this.wellKnownTypes = wellKnownTypes;
        }

        private readonly ConcurrentDictionary<ITypeSymbol, bool> ShallowCopyableTypes = new ConcurrentDictionary<ITypeSymbol, bool>(SymbolEqualityComparer.Default);

        /// <summary>
        /// Returns the name of the generated class for the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>The name of the generated class for the provided type.</returns>
        internal string GetGeneratedClassName(INamedTypeSymbol type)
        {
            var parts = type.ToDisplayParts(SymbolDisplayFormat.FullyQualifiedFormat
                .WithGlobalNamespaceStyle(SymbolDisplayGlobalNamespaceStyle.Omitted)
                .WithMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.None)
                .WithGenericsOptions(SymbolDisplayGenericsOptions.None)
                .WithKindOptions(SymbolDisplayKindOptions.None)
                .WithLocalOptions(SymbolDisplayLocalOptions.None)
                .WithMemberOptions(SymbolDisplayMemberOptions.None)
                .WithParameterOptions(SymbolDisplayParameterOptions.None));
            var b = new StringBuilder();
            foreach (var part in parts)
            {
                // Add the class prefix after the type name.
                switch (part.Kind)
                {
                    case SymbolDisplayPartKind.Punctuation:
                        b.Append('_');
                        break;
                    case SymbolDisplayPartKind.ClassName:
                    case SymbolDisplayPartKind.InterfaceName:
                    case SymbolDisplayPartKind.StructName:
                        b.Append(part.ToString().TrimStart('@'));
                        b.Append(ClassSuffix);
                        break;
                    default:
                        b.Append(part.ToString().TrimStart('@'));
                        break;
                }
            }

            return CodeGenerator.ToolName + b;
        }

        /// <summary>
        /// Generates the non serializer class for the provided grain types.
        /// </summary>
        internal (TypeDeclarationSyntax, TypeSyntax) GenerateClass(IGeneratorExecutionContext context, SemanticModel model, SerializerTypeDescription description)
        {
            var className = GetGeneratedClassName(description.Target);
            var type = description.Target;
            var genericTypes = type.GetHierarchyTypeParameters().Select(_ => TypeParameter(_.ToString())).ToArray();

            var attributes = new List<AttributeSyntax>
            {
                GeneratedCodeAttributeGenerator.GetGeneratedCodeAttributeSyntax(wellKnownTypes),
                Attribute(wellKnownTypes.ExcludeFromCodeCoverageAttribute.ToNameSyntax()),
                Attribute(wellKnownTypes.SerializerAttribute.ToNameSyntax())
                    .AddArgumentListArguments(
                        AttributeArgument(TypeOfExpression(type.WithoutTypeParameters().ToTypeSyntax())))
            };

            var fields = GetFields(context, model, type);

            var members = new List<MemberDeclarationSyntax>(GenerateFields(fields))
            {
                GenerateConstructor(className, fields),
                GenerateDeepCopierMethod(type, fields, model),
                GenerateSerializerMethod(type, fields, model),
                GenerateDeserializerMethod(type, fields, model),
            };

            var classDeclaration =
                ClassDeclaration(className)
                    .AddModifiers(Token(SyntaxKind.InternalKeyword))
                    .AddModifiers(Token(SyntaxKind.SealedKeyword))
                    .AddAttributeLists(AttributeList().AddAttributes(attributes.ToArray()))
                    .AddMembers(members.ToArray())
                    .AddConstraintClauses(type.GetTypeConstraintSyntax());

            if (genericTypes.Length > 0)
            {
                classDeclaration = classDeclaration.AddTypeParameterListParameters(genericTypes);
            }

            if (this.options.DebuggerStepThrough)
            {
                var debuggerStepThroughAttribute = Attribute(this.wellKnownTypes.DebuggerStepThroughAttribute.ToNameSyntax());
                classDeclaration = classDeclaration.AddAttributeLists(AttributeList().AddAttributes(debuggerStepThroughAttribute));
            }

            return (classDeclaration, ParseTypeName(type.GetParsableReplacementName(className)));
        }

        private MemberDeclarationSyntax GenerateConstructor(string className, List<FieldInfoMember> fields)
        {
            var body = new List<StatementSyntax>();

            // Expressions for specifying binding flags.
            var bindingFlags = SymbolSyntaxExtensions.GetBindingFlagsParenthesizedExpressionSyntax(
                SyntaxKind.BitwiseOrExpression,
                BindingFlags.Instance,
                BindingFlags.NonPublic,
                BindingFlags.Public);

            var fieldUtils = IdentifierName("fieldUtils");

            foreach (var field in fields)
            {
                // Get the field
                var fieldInfoField = IdentifierName(field.InfoFieldName);
                var fieldInfo =
                    InvocationExpression(TypeOfExpression(field.Field.ContainingType.ToTypeSyntax()).Member("GetField"))
                        .AddArgumentListArguments(
                            Argument(field.Field.Name.ToLiteralExpression()),
                            Argument(bindingFlags));
                var fieldInfoVariable =
                    VariableDeclarator(field.InfoFieldName).WithInitializer(EqualsValueClause(fieldInfo));

                if (!field.IsGettableProperty || !field.IsSettableProperty)
                {
                    body.Add(LocalDeclarationStatement(
                        VariableDeclaration(wellKnownTypes.FieldInfo.ToTypeSyntax()).AddVariables(fieldInfoVariable)));
                }

                // Set the getter/setter of the field
                if (!field.IsGettableProperty)
                {
                    var getterType = wellKnownTypes.Func_2.Construct(field.Field.ContainingType, field.SafeType).ToTypeSyntax();

                    var getterInvoke = CastExpression(
                        getterType,
                        InvocationExpression(fieldUtils.Member("GetGetter")).AddArgumentListArguments(Argument(fieldInfoField)));

                    body.Add(ExpressionStatement(
                        AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(field.GetterFieldName), getterInvoke)));
                }

                if (!field.IsSettableProperty)
                {
                    if (field.Field.ContainingType != null && field.Field.ContainingType.IsValueType)
                    {
                        var setterType = wellKnownTypes.ValueTypeSetter_2.Construct(field.Field.ContainingType, field.SafeType).ToTypeSyntax();

                        var getValueSetterInvoke = CastExpression(
                            setterType,
                            InvocationExpression(fieldUtils.Member("GetValueSetter"))
                                .AddArgumentListArguments(Argument(fieldInfoField)));

                        body.Add(ExpressionStatement(
                            AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(field.SetterFieldName), getValueSetterInvoke)));
                    }
                    else
                    {
                        var setterType = wellKnownTypes.Action_2.Construct(field.Field.ContainingType, field.SafeType).ToTypeSyntax();

                        var getReferenceSetterInvoke = CastExpression(
                            setterType,
                            InvocationExpression(fieldUtils.Member("GetReferenceSetter"))
                                .AddArgumentListArguments(Argument(fieldInfoField)));

                        body.Add(ExpressionStatement(
                            AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, IdentifierName(field.SetterFieldName), getReferenceSetterInvoke)));
                    }
                }

            }

            return
                ConstructorDeclaration(className)
                    .AddModifiers(Token(SyntaxKind.PublicKeyword))
                    .AddParameterListParameters(
                        Parameter(fieldUtils.Identifier).WithType(wellKnownTypes.IFieldUtils.ToTypeSyntax()))
                    .AddBodyStatements(body.ToArray());
        }

        /// <summary>
        /// Returns syntax for the deserializer method.
        /// </summary>
        private MemberDeclarationSyntax GenerateDeserializerMethod(INamedTypeSymbol type, List<FieldInfoMember> fields, SemanticModel model)
        {
            var contextParameter = IdentifierName("context");

            var resultDeclaration =
                LocalDeclarationStatement(
                    VariableDeclaration(type.ToTypeSyntax())
                        .AddVariables(
                            VariableDeclarator("result")
                                .WithInitializer(EqualsValueClause(GetObjectCreationExpressionSyntax(type, model)))));
            var resultVariable = IdentifierName("result");

            var body = new List<StatementSyntax> { resultDeclaration };

            // Value types cannot be referenced, only copied, so there is no need to box & record instances of value types.
            if (!type.IsValueType)
            {
                // Record the result for cyclic deserialization.
                var currentSerializationContext = contextParameter;
                body.Add(
                    ExpressionStatement(
                        InvocationExpression(currentSerializationContext.Member("RecordObject"))
                            .AddArgumentListArguments(Argument(resultVariable))));
            }

            // Deserialize all fields.
            foreach (var field in fields)
            {
                var deserialized =
                    InvocationExpression(contextParameter.Member("DeserializeInner"))
                        .AddArgumentListArguments(
                            Argument(TypeOfExpression(field.Type)));
                body.Add(
                    ExpressionStatement(
                        field.GetSetter(
                            resultVariable,
                            CastExpression(field.Type, deserialized))));
            }

            // If the type implements the internal IOnDeserialized lifecycle method, invoke it's method now.
            if (type.HasInterface(wellKnownTypes.IOnDeserialized))
            {
                // C#: ((IOnDeserialized)result).OnDeserialized(context);
                var typedResult = ParenthesizedExpression(CastExpression(wellKnownTypes.IOnDeserialized.ToTypeSyntax(), resultVariable));
                var invokeOnDeserialized = InvocationExpression(typedResult.Member("OnDeserialized"))
                    .AddArgumentListArguments(Argument(contextParameter));
                body.Add(ExpressionStatement(invokeOnDeserialized));
            }

            body.Add(ReturnStatement(CastExpression(type.ToTypeSyntax(), resultVariable)));
            return
                MethodDeclaration(wellKnownTypes.Object.ToTypeSyntax(), "Deserializer")
                    .AddModifiers(Token(SyntaxKind.PublicKeyword))
                    .AddParameterListParameters(
                        Parameter(Identifier("expected")).WithType(wellKnownTypes.Type.ToTypeSyntax()),
                        Parameter(Identifier("context")).WithType(wellKnownTypes.IDeserializationContext.ToTypeSyntax()))
                    .AddBodyStatements(body.ToArray())
                    .AddAttributeLists(
                        AttributeList()
                            .AddAttributes(Attribute(wellKnownTypes.DeserializerMethodAttribute.ToNameSyntax())));
        }

        private MemberDeclarationSyntax GenerateSerializerMethod(INamedTypeSymbol type, List<FieldInfoMember> fields, SemanticModel model)
        {
            var contextParameter = IdentifierName("context");

            var body = new List<StatementSyntax>
            {
                LocalDeclarationStatement(
                    VariableDeclaration(type.ToTypeSyntax())
                        .AddVariables(
                            VariableDeclarator("input")
                                .WithInitializer(
                                    EqualsValueClause(
                                        CastExpression(type.ToTypeSyntax(), IdentifierName("untypedInput"))))))
            };

            var inputExpression = IdentifierName("input");

            // Serialize all members.
            foreach (var field in fields)
            {
                body.Add(
                    ExpressionStatement(
                        InvocationExpression(contextParameter.Member("SerializeInner"))
                            .AddArgumentListArguments(
                                Argument(field.GetGetter(inputExpression, forceAvoidCopy: true)),
                                Argument(TypeOfExpression(field.Type)))));
            }

            return
                MethodDeclaration(wellKnownTypes.Void.ToTypeSyntax(), "Serializer")
                    .AddModifiers(Token(SyntaxKind.PublicKeyword))
                    .AddParameterListParameters(
                        Parameter(Identifier("untypedInput")).WithType(wellKnownTypes.Object.ToTypeSyntax()),
                        Parameter(Identifier("context")).WithType(wellKnownTypes.ISerializationContext.ToTypeSyntax()),
                        Parameter(Identifier("expected")).WithType(wellKnownTypes.Type.ToTypeSyntax()))
                    .AddBodyStatements(body.ToArray())
                    .AddAttributeLists(
                        AttributeList()
                            .AddAttributes(Attribute(wellKnownTypes.SerializerMethodAttribute.ToNameSyntax())));
        }

        /// <summary>
        /// Returns syntax for the deep copy method.
        /// </summary>
        private MemberDeclarationSyntax GenerateDeepCopierMethod(INamedTypeSymbol type, List<FieldInfoMember> fields, SemanticModel model)
        {
            var originalVariable = IdentifierName("original");

            var body = new List<StatementSyntax>();
            if (type.HasAttribute(wellKnownTypes.ImmutableAttribute)
                || SymbolEqualityComparer.Default.Equals(wellKnownTypes.Immutable_1, type))
            {
                // Immutable types do not require copying.
                var typeName = type.ToDisplayString();
                var comment = Comment($"// No deep copy required since {typeName} is marked with the [Immutable] attribute.");
                body.Add(ReturnStatement(originalVariable).WithLeadingTrivia(comment));
            }
            else
            {
                var inputVariable = IdentifierName("input");
                body.Add(
                    LocalDeclarationStatement(
                        VariableDeclaration(type.ToTypeSyntax())
                            .AddVariables(
                                VariableDeclarator("input")
                                    .WithInitializer(
                                        EqualsValueClause(
                                            ParenthesizedExpression(
                                                CastExpression(type.ToTypeSyntax(), originalVariable)))))));

                if (IsOrleansShallowCopyable(type))
                {
                    var comment = Comment($"// {type.ToDisplayString()} needs only a shallow copy.");
                    body.Add(ReturnStatement(inputVariable).WithLeadingTrivia(comment));
                }
                else
                {
                    var resultVariable = IdentifierName("result");
                    body.Add(
                    LocalDeclarationStatement(
                        VariableDeclaration(type.ToTypeSyntax())
                            .AddVariables(
                                VariableDeclarator("result")
                                    .WithInitializer(EqualsValueClause(GetObjectCreationExpressionSyntax(type, model))))));

                    var context = IdentifierName("context");
                    if (!type.IsValueType)
                    {
                        // Record this serialization.
                        body.Add(
                            ExpressionStatement(
                                InvocationExpression(context.Member("RecordCopy"))
                                    .AddArgumentListArguments(Argument(originalVariable), Argument(resultVariable))));
                    }

                    // Copy all members from the input to the result.
                    foreach (var field in fields)
                    {
                        body.Add(ExpressionStatement(field.GetSetter(resultVariable, field.GetGetter(inputVariable, context))));
                    }

                    body.Add(ReturnStatement(resultVariable));
                }
            }

            return
                MethodDeclaration(wellKnownTypes.Object.ToTypeSyntax(), "DeepCopier")
                    .AddModifiers(Token(SyntaxKind.PublicKeyword))
                    .AddParameterListParameters(
                        Parameter(Identifier("original")).WithType(wellKnownTypes.Object.ToTypeSyntax()),
                        Parameter(Identifier("context")).WithType(wellKnownTypes.ICopyContext.ToTypeSyntax()))
                    .AddBodyStatements(body.ToArray())
                    .AddAttributeLists(
                        AttributeList().AddAttributes(Attribute(wellKnownTypes.CopierMethodAttribute.ToNameSyntax())));
        }

        /// <summary>
        /// Returns syntax for the fields of the serializer class.
        /// </summary>
        private MemberDeclarationSyntax[] GenerateFields(List<FieldInfoMember> fields)
        {
            var result = new List<MemberDeclarationSyntax>();

            // Add each field and initialize it.
            foreach (var field in fields)
            {
                // Declare the getter for this field.
                if (!field.IsGettableProperty)
                {
                    var getterType = wellKnownTypes.Func_2.Construct(field.Field.ContainingType, field.SafeType).ToTypeSyntax();
                    var fieldGetterVariable = VariableDeclarator(field.GetterFieldName);

                    result.Add(
                        FieldDeclaration(VariableDeclaration(getterType).AddVariables(fieldGetterVariable))
                            .AddModifiers(
                                Token(SyntaxKind.PrivateKeyword),
                                Token(SyntaxKind.ReadOnlyKeyword)));
                }

                if (!field.IsSettableProperty)
                {
                    if (field.Field.ContainingType != null && field.Field.ContainingType.IsValueType)
                    {
                        var setterType = wellKnownTypes.ValueTypeSetter_2.Construct(field.Field.ContainingType, field.SafeType).ToTypeSyntax();
                        var fieldSetterVariable = VariableDeclarator(field.SetterFieldName);

                        result.Add(
                            FieldDeclaration(VariableDeclaration(setterType).AddVariables(fieldSetterVariable))
                                .AddModifiers(
                                    Token(SyntaxKind.PrivateKeyword),
                                    Token(SyntaxKind.ReadOnlyKeyword)));
                    }
                    else
                    {
                        var setterType = wellKnownTypes.Action_2.Construct(field.Field.ContainingType, field.SafeType).ToTypeSyntax();
                        var fieldSetterVariable = VariableDeclarator(field.SetterFieldName);

                        result.Add(
                            FieldDeclaration(VariableDeclaration(setterType).AddVariables(fieldSetterVariable))
                                .AddModifiers(
                                    Token(SyntaxKind.PrivateKeyword),
                                    Token(SyntaxKind.ReadOnlyKeyword)));
                    }
                }
            }

            return result.ToArray();
        }


        /// <summary>
        /// Returns syntax for initializing a new instance of the provided type.
        /// </summary>
        private ExpressionSyntax GetObjectCreationExpressionSyntax(INamedTypeSymbol type, SemanticModel model)
        {
            ExpressionSyntax result;

            if (type.IsValueType)
            {
                // Use the default value.
                result = DefaultExpression(type.ToTypeSyntax());
            }
            else if (GetEmptyConstructor(type, model) != null)
            {
                // Use the default constructor.
                result = ObjectCreationExpression(type.ToTypeSyntax()).AddArgumentListArguments();
            }
            else
            {
                // Create an unformatted object.
                result = CastExpression(
                    type.ToTypeSyntax(),
                    InvocationExpression(wellKnownTypes.FormatterServices.ToTypeSyntax().Member("GetUninitializedObject"))
                        .AddArgumentListArguments(
                            Argument(TypeOfExpression(type.ToTypeSyntax()))));
            }

            return result;
        }

        /// <summary>
        /// Return the default constructor on <paramref name="type"/> if found or null if not found.
        /// </summary>
        private IMethodSymbol GetEmptyConstructor(INamedTypeSymbol type, SemanticModel model)
        {
            return type.GetDeclaredInstanceMembers<IMethodSymbol>()
                .FirstOrDefault(method => method.MethodKind == MethodKind.Constructor && method.Parameters.Length == 0 && model.IsAccessible(0, method));
        }

        /// <summary>
        /// Returns a sorted list of the fields of the provided type.
        /// </summary>
        private List<FieldInfoMember> GetFields(IGeneratorExecutionContext context, SemanticModel model, INamedTypeSymbol type)
        {
            var result = new List<FieldInfoMember>();
            foreach (var field in type.GetDeclaredInstanceMembers<IFieldSymbol>())
            {
                if (ShouldSerializeField(field))
                {
                    result.Add(new FieldInfoMember(this, model, type, field, result.Count));
                }
            }

            if (type.TypeKind == TypeKind.Class)
            {
                // Some reference assemblies are compiled without private fields.
                // Warn the user if they are inheriting from a type in one of these assemblies using a heuristic:
                // If the type inherits from a type in a reference assembly and there are no fields declared on those
                // base types, emit a warning.
                var hasUnsupportedRefAsmBase = false;
                var referenceAssemblyHasFields = false;
                var baseType = type.BaseType;
                while (baseType != null &&
                       !SymbolEqualityComparer.Default.Equals(baseType, wellKnownTypes.Object) &&
                       !SymbolEqualityComparer.Default.Equals(baseType, wellKnownTypes.Attribute))
                {
                    if (!hasUnsupportedRefAsmBase
                        && baseType.ContainingAssembly.HasAttribute("ReferenceAssemblyAttribute")
                        && !IsSupportedRefAsmType(baseType))
                    {
                        hasUnsupportedRefAsmBase = true;
                    }
                    foreach (var field in baseType.GetDeclaredInstanceMembers<IFieldSymbol>())
                    {
                        if (hasUnsupportedRefAsmBase) referenceAssemblyHasFields = true;
                        if (ShouldSerializeField(field))
                        {
                            result.Add(new FieldInfoMember(this, model, type, field, result.Count));
                        }
                    }

                    baseType = baseType.BaseType;
                }

                if (hasUnsupportedRefAsmBase && !referenceAssemblyHasFields)
                {
                    var declaration = type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as TypeDeclarationSyntax;
                    context.ReportDiagnostic(RefAssemblyBaseTypeDiagnosticAnalyzer.CreateDiagnostic(declaration));
                }

                bool IsSupportedRefAsmType(INamedTypeSymbol t)
                {
                    INamedTypeSymbol baseDefinition;
                    if (t.IsGenericType && !t.IsUnboundGenericType)
                    {
                        baseDefinition = t.ConstructUnboundGenericType().OriginalDefinition;
                    }
                    else
                    {
                        baseDefinition = t.OriginalDefinition;
                    }

                    foreach (var refAsmType in wellKnownTypes.SupportedRefAsmBaseTypes)
                    {
                        if (SymbolEqualityComparer.Default.Equals(baseDefinition, refAsmType)) return true;
                    }

                    return false;
                }
            }

            result.Sort(FieldInfoMember.Comparer.Instance);
            return result;
        }

        /// <summary>
        /// Returns <see langowrd="true"/> if the provided field should be serialized, <see langword="false"/> otherwise.
        /// </summary>
        public bool ShouldSerializeField(IFieldSymbol symbol)
        {
            if (symbol.IsStatic) return false;
            if (symbol.HasAttribute(wellKnownTypes.NonSerializedAttribute)) return false;

            ITypeSymbol fieldType = symbol.Type;

            if (fieldType.TypeKind == TypeKind.Pointer) return false;
            if (fieldType.TypeKind == TypeKind.Delegate) return false;

            if (fieldType.SpecialType == SpecialType.System_IntPtr) return false;
            if (fieldType.SpecialType == SpecialType.System_UIntPtr) return false;

            if (symbol.ContainingType.HasBaseType(wellKnownTypes.MarshalByRefObject)) return false;

            return true;
        }

        internal bool IsOrleansShallowCopyable(ITypeSymbol type)
        {
            switch (type.SpecialType)
            {
                case SpecialType.System_Boolean:
                case SpecialType.System_Char:
                case SpecialType.System_SByte:
                case SpecialType.System_Byte:
                case SpecialType.System_Int16:
                case SpecialType.System_UInt16:
                case SpecialType.System_Int32:
                case SpecialType.System_UInt32:
                case SpecialType.System_Int64:
                case SpecialType.System_UInt64:
                case SpecialType.System_Decimal:
                case SpecialType.System_Single:
                case SpecialType.System_Double:
                case SpecialType.System_String:
                case SpecialType.System_DateTime:
                    return true;
            }

            if (SymbolEqualityComparer.Default.Equals(wellKnownTypes.TimeSpan, type)
                || SymbolEqualityComparer.Default.Equals(wellKnownTypes.IPAddress, type)
                || SymbolEqualityComparer.Default.Equals(wellKnownTypes.IPEndPoint, type)
                || SymbolEqualityComparer.Default.Equals(wellKnownTypes.SiloAddress, type)
                || SymbolEqualityComparer.Default.Equals(wellKnownTypes.GrainId, type)
                || SymbolEqualityComparer.Default.Equals(wellKnownTypes.ActivationId, type)
                || SymbolEqualityComparer.Default.Equals(wellKnownTypes.ActivationAddress, type)
                || wellKnownTypes.CorrelationId is WellKnownTypes.Some correlationIdType && SymbolEqualityComparer.Default.Equals(correlationIdType.Value, type)
                || SymbolEqualityComparer.Default.Equals(wellKnownTypes.CancellationToken, type)
                || SymbolEqualityComparer.Default.Equals(wellKnownTypes.Type, type)) return true;

            if (ShallowCopyableTypes.TryGetValue(type, out var result)) return result;

            if (type.HasAttribute(wellKnownTypes.ImmutableAttribute))
            {
                return ShallowCopyableTypes[type] = true;
            }

            if (type.HasBaseType(wellKnownTypes.Exception))
            {
                return ShallowCopyableTypes[type] = true;
            }

            if (!(type is INamedTypeSymbol namedType))
            {
                return ShallowCopyableTypes[type] = false;
            }

            if (namedType.IsTupleType)
            {
                return ShallowCopyableTypes[type] = namedType.TupleElements.All(f => IsOrleansShallowCopyable(f.Type));
            }
            else if (namedType.IsGenericType)
            {
                var def = namedType.ConstructedFrom;
                if (def.SpecialType == SpecialType.System_Nullable_T)
                {
                    return ShallowCopyableTypes[type] = IsOrleansShallowCopyable(namedType.TypeArguments.Single());
                }

                if (SymbolEqualityComparer.Default.Equals(wellKnownTypes.Immutable_1, def))
                {
                    return ShallowCopyableTypes[type] = true;
                }

                if (wellKnownTypes.TupleTypes.Any(t => SymbolEqualityComparer.Default.Equals(t, def)))
                {
                    return ShallowCopyableTypes[type] = namedType.TypeArguments.All(IsOrleansShallowCopyable);
                }
            }
            else
            {
                if (type.TypeKind == TypeKind.Enum)
                {
                    return ShallowCopyableTypes[type] = true;
                }

                if (type.TypeKind == TypeKind.Struct && !namedType.IsUnboundGenericType)
                {
                    return ShallowCopyableTypes[type] = IsValueTypeFieldsShallowCopyable(type);
                }
            }

            return ShallowCopyableTypes[type] = false;
        }

        private bool IsValueTypeFieldsShallowCopyable(ITypeSymbol type)
        {
            foreach (var field in type.GetInstanceMembers<IFieldSymbol>())
            {
                if (field.IsStatic) continue;

                if (!(field.Type is INamedTypeSymbol fieldType))
                {
                    return false;
                }

                if (SymbolEqualityComparer.Default.Equals(type, fieldType)) return false;

                if (!IsOrleansShallowCopyable(fieldType)) return false;
            }

            return true;
        }

        /// <summary>
        /// Represents a field.
        /// </summary>
        private class FieldInfoMember
        {
            private readonly SerializerGenerator generator;
            private readonly SemanticModel model;
            private readonly WellKnownTypes wellKnownTypes;
            private readonly INamedTypeSymbol targetType;
            private IPropertySymbol property;

            /// <summary>
            /// The ordinal assigned to this field.
            /// </summary>
            private readonly int ordinal;

            public FieldInfoMember(SerializerGenerator generator, SemanticModel model, INamedTypeSymbol targetType, IFieldSymbol field, int ordinal)
            {
                this.generator = generator;
                this.wellKnownTypes = generator.wellKnownTypes;
                this.model = model;
                this.targetType = targetType;
                this.Field = field;
                this.ordinal = ordinal;
            }

            /// <summary>
            /// Gets the underlying <see cref="Field"/> instance.
            /// </summary>
            public IFieldSymbol Field { get; }

            /// <summary>
            /// Gets a usable representation of the field type.
            /// </summary>
            /// <remarks>
            /// If the field is of type 'dynamic', we represent it as 'object' because 'dynamic' cannot appear in typeof expressions.
            /// </remarks>
            public ITypeSymbol SafeType => this.Field.Type.TypeKind == TypeKind.Dynamic
                ? this.wellKnownTypes.Object
                : this.Field.Type;

            /// <summary>
            /// Gets the name of the field info field.
            /// </summary>
            public string InfoFieldName => "field" + this.ordinal;

            /// <summary>
            /// Gets the name of the getter field.
            /// </summary>
            public string GetterFieldName => "getField" + this.ordinal;

            /// <summary>
            /// Gets the name of the setter field.
            /// </summary>
            public string SetterFieldName => "setField" + this.ordinal;

            /// <summary>
            /// Gets a value indicating whether or not this field represents a property with an accessible, non-obsolete getter. 
            /// </summary>
            public bool IsGettableProperty => this.Property?.GetMethod != null && this.model.IsAccessible(0, this.Property.GetMethod) && !this.IsObsolete;

            /// <summary>
            /// Gets a value indicating whether or not this field represents a property with an accessible, non-obsolete setter. 
            /// </summary>
            public bool IsSettableProperty => this.Property?.SetMethod != null && this.model.IsAccessible(0, this.Property.SetMethod) && !this.IsObsolete;

            /// <summary>
            /// Gets syntax representing the type of this field.
            /// </summary>
            public TypeSyntax Type => this.SafeType.ToTypeSyntax();

            /// <summary>
            /// Gets the <see cref="Property"/> which this field is the backing property for, or
            /// <see langword="null" /> if this is not the backing field of an auto-property.
            /// </summary>
            private IPropertySymbol Property
            {
                get
                {
                    if (this.property != null)
                    {
                        return this.property;
                    }

                    var propertyName = Regex.Match(this.Field.Name, "^<([^>]+)>.*$");
                    if (!propertyName.Success || this.Field.ContainingType == null) return null;

                    var name = propertyName.Groups[1].Value;
                    var candidates = this.targetType
                        .GetInstanceMembers<IPropertySymbol>()
                        .Where(p => string.Equals(name, p.Name, StringComparison.Ordinal) && !p.IsAbstract)
                        .ToArray();

                    if (candidates.Length > 1) return null;
                    if (!SymbolEqualityComparer.Default.Equals(this.SafeType, candidates[0].Type)) return null;

                    return this.property = candidates[0];
                }
            }

            /// <summary>
            /// Gets a value indicating whether or not this field is obsolete.
            /// </summary>
            private bool IsObsolete => this.Field.HasAttribute(this.wellKnownTypes.ObsoleteAttribute) ||
                                       this.Property != null && this.Property.HasAttribute(this.wellKnownTypes.ObsoleteAttribute);

            /// <summary>
            /// Returns syntax for retrieving the value of this field, deep copying it if necessary.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <param name="serializationContextExpression">The expression used to retrieve the serialization context.</param>
            /// <param name="forceAvoidCopy">Whether or not to ensure that no copy of the field is made.</param>
            /// <returns>Syntax for retrieving the value of this field.</returns>
            public ExpressionSyntax GetGetter(ExpressionSyntax instance, ExpressionSyntax serializationContextExpression = null, bool forceAvoidCopy = false)
            {
                // Retrieve the value of the field.
                var getValueExpression = this.GetValueExpression(instance);

                // Avoid deep-copying the field if possible.
                if (forceAvoidCopy || generator.IsOrleansShallowCopyable(this.SafeType))
                {
                    // Return the value without deep-copying it.
                    return getValueExpression;
                }

                // Addressable arguments must be converted to references before passing.
                // IGrainObserver instances cannot be directly converted to references, therefore they are not included.
                ExpressionSyntax deepCopyValueExpression;
                if (this.SafeType.HasInterface(this.wellKnownTypes.IAddressable) &&
                    this.SafeType.TypeKind == TypeKind.Interface &&
                    !this.SafeType.HasInterface(this.wellKnownTypes.IGrainObserver))
                {
                    var getAsReference = getValueExpression.Member("AsReference".ToGenericName().AddTypeArgumentListArguments(this.Type));

                    // If the value is not a GrainReference, convert it to a strongly-typed GrainReference.
                    // C#: (value == null || value is GrainReference) ? value : value.AsReference<TInterface>()
                    deepCopyValueExpression =
                        ConditionalExpression(
                            ParenthesizedExpression(
                                BinaryExpression(
                                    SyntaxKind.LogicalOrExpression,
                                    BinaryExpression(
                                        SyntaxKind.EqualsExpression,
                                        getValueExpression,
                                        LiteralExpression(SyntaxKind.NullLiteralExpression)),
                                    BinaryExpression(
                                        SyntaxKind.IsExpression,
                                        getValueExpression,
                                        this.wellKnownTypes.GrainReference.ToTypeSyntax()))),
                            getValueExpression,
                            InvocationExpression(getAsReference));
                }
                else
                {
                    deepCopyValueExpression = getValueExpression;
                }

                // Deep-copy the value.
                return CastExpression(
                    this.Type,
                    InvocationExpression(serializationContextExpression.Member("DeepCopyInner"))
                        .AddArgumentListArguments(
                            Argument(deepCopyValueExpression)));
            }

            /// <summary>
            /// Returns syntax for setting the value of this field.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <param name="value">Syntax for the new value.</param>
            /// <returns>Syntax for setting the value of this field.</returns>
            public ExpressionSyntax GetSetter(ExpressionSyntax instance, ExpressionSyntax value)
            {
                // If the field is the backing field for an accessible auto-property use the property directly.
                if (this.IsSettableProperty)
                {
                    return AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        instance.Member(this.Property.Name),
                        value);
                }

                var instanceArg = Argument(instance);
                if (this.Field.ContainingType != null && this.Field.ContainingType.IsValueType)
                {
                    instanceArg = instanceArg.WithRefOrOutKeyword(Token(SyntaxKind.RefKeyword));
                }

                return
                    InvocationExpression(IdentifierName(this.SetterFieldName))
                        .AddArgumentListArguments(instanceArg, Argument(value));
            }

            /// <summary>
            /// Returns syntax for retrieving the value of this field.
            /// </summary>
            /// <param name="instance">The instance of the containing type.</param>
            /// <returns>Syntax for retrieving the value of this field.</returns>
            private ExpressionSyntax GetValueExpression(ExpressionSyntax instance)
            {
                // If the field is the backing field for an accessible auto-property use the property directly.
                ExpressionSyntax result;
                if (this.IsGettableProperty)
                {
                    result = instance.Member(this.Property.Name);
                }
                else
                {
                    // Retrieve the field using the generated getter.
                    result =
                        InvocationExpression(IdentifierName(this.GetterFieldName))
                            .AddArgumentListArguments(Argument(instance));
                }

                return result;
            }

            /// <summary>
            /// A comparer for <see cref="FieldInfoMember"/> which compares by name.
            /// </summary>
            public class Comparer : IComparer<FieldInfoMember>
            {
                /// <summary>
                /// Gets the singleton instance of this class.
                /// </summary>
                public static Comparer Instance { get; } = new Comparer();

                public int Compare(FieldInfoMember x, FieldInfoMember y)
                {
                    return string.Compare(x?.Field.Name, y?.Field.Name, StringComparison.Ordinal);
                }
            }
        }
    }
}