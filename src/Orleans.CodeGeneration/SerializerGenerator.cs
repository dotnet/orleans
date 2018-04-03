namespace Orleans.CodeGenerator
{
    using System;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.Serialization;
    using System.Text.RegularExpressions;
    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Orleans.CodeGeneration;
    using Orleans.CodeGenerator.Utilities;
    using Orleans.Concurrency;
    using Orleans.Runtime;
    using Orleans.Serialization;
    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
    using static Microsoft.CodeAnalysis.SyntaxNodeExtensions;

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
    public static class SerializerGenerator
    {
        private static readonly TypeFormattingOptions GeneratedTypeNameOptions = new TypeFormattingOptions(
            ClassSuffix,
            includeGenericParameters: false,
            includeTypeParameters: false,
            nestedClassSeparator: '_',
            includeGlobal: false);

        /// <summary>
        /// The suffix appended to the name of generated classes.
        /// </summary>
        private const string ClassSuffix = "Serializer";

        private static readonly RuntimeTypeHandle IntPtrTypeHandle = typeof(IntPtr).TypeHandle;
        private static readonly RuntimeTypeHandle UIntPtrTypeHandle = typeof(UIntPtr).TypeHandle;
        private static readonly RuntimeTypeHandle MarshalByRefObjectType = typeof(MarshalByRefObject).TypeHandle;
        private static readonly Type DelegateType = typeof(Delegate);

        /// <summary>
        /// Returns the name of the generated class for the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>The name of the generated class for the provided type.</returns>
        internal static string GetGeneratedClassName(Type type) => CodeGeneratorCommon.ClassPrefix + type.GetParseableName(GeneratedTypeNameOptions);

        /// <summary>
        /// Generates the non static serializer class for the provided grain types.
        /// 
        /// </summary>
        /// <param name="className">The name for the generated class.</param>
        /// <param name="type">The type which this serializer is being generated for.</param>
        /// <param name="onEncounteredType">
        /// The callback invoked when a type is encountered.
        /// </param>
        /// <returns>
        /// The generated class.
        /// </returns>
        internal static TypeDeclarationSyntax GenerateClass(string className, Type type, Action<Type> onEncounteredType)
        {
            var typeInfo = type;
            var genericTypes = typeInfo.IsGenericTypeDefinition
                                   ? typeInfo.GetGenericArguments().Select(_ => SF.TypeParameter(_.ToString())).ToArray()
                                   : new TypeParameterSyntax[0];

            var attributes = new List<AttributeSyntax>
            {
                CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
                SF.Attribute(typeof(ExcludeFromCodeCoverageAttribute).GetNameSyntax()),
                SF.Attribute(typeof(SerializerAttribute).GetNameSyntax())
                    .AddArgumentListArguments(
                        SF.AttributeArgument(SF.TypeOfExpression(type.GetTypeSyntax(includeGenericParameters: false))))
            };
            
            var fields = GetFields(type);

            // Mark each field type for generation
            foreach (var field in fields)
            {
                var fieldType = field.FieldInfo.FieldType;
                onEncounteredType(fieldType);
            }

            var members = new List<MemberDeclarationSyntax>(GenerateFields(fields))
            {
                GenerateConstructor(className, fields),
                GenerateDeepCopierMethod(type, fields),
                GenerateSerializerMethod(type, fields),
                GenerateDeserializerMethod(type, fields),
            };
            
            var classDeclaration =
                SF.ClassDeclaration(className)
                    .AddModifiers(SF.Token(SyntaxKind.InternalKeyword))
                    .AddModifiers(SF.Token(SyntaxKind.SealedKeyword))
                    .AddAttributeLists(SF.AttributeList().AddAttributes(attributes.ToArray()))
                    .AddMembers(members.ToArray())
                    .AddConstraintClauses(type.GetTypeConstraintSyntax());
            if (genericTypes.Length > 0)
            {
                classDeclaration = classDeclaration.AddTypeParameterListParameters(genericTypes);
            }

            return classDeclaration;
        }

        private static MemberDeclarationSyntax GenerateConstructor(string className, List<FieldInfoMember> fields)
        {
            var body = new List<StatementSyntax>();

            Expression<Action<TypeInfo>> getField = _ => _.GetField(string.Empty, BindingFlags.Default);
            Expression<Action<IFieldUtils>> getGetter = _ => _.GetGetter(default(FieldInfo));
            Expression<Action<IFieldUtils>> getReferenceSetter = _ => _.GetReferenceSetter(default(FieldInfo));
            Expression<Action<IFieldUtils>> getValueSetter = _ => _.GetValueSetter(default(FieldInfo));

            // Expressions for specifying binding flags.
            var bindingFlags = SyntaxFactoryExtensions.GetBindingFlagsParenthesizedExpressionSyntax(
                   SyntaxKind.BitwiseOrExpression,
                   BindingFlags.Instance,
                   BindingFlags.NonPublic,
                   BindingFlags.Public);

            var fieldUtils = SF.IdentifierName("fieldUtils");

            foreach (var field in fields)
            {
                // Get the field
                var fieldInfoField = SF.IdentifierName(field.InfoFieldName);
                var fieldInfo =
                    getField.Invoke(SF.TypeOfExpression(field.FieldInfo.DeclaringType.GetTypeSyntax()))
                        .AddArgumentListArguments(
                            SF.Argument(field.FieldInfo.Name.GetLiteralExpression()),
                            SF.Argument(bindingFlags));
                var fieldInfoVariable =
                    SF.VariableDeclarator(field.InfoFieldName).WithInitializer(SF.EqualsValueClause(fieldInfo));

                if (!field.IsGettableProperty || !field.IsSettableProperty)
                {
                    body.Add(SF.LocalDeclarationStatement(
                            SF.VariableDeclaration(typeof(FieldInfo).GetTypeSyntax()).AddVariables(fieldInfoVariable)));
                }

                // Set the getter/setter of the field
                if (!field.IsGettableProperty)
                {
                    var getterType =
                        typeof(Func<,>).MakeGenericType(field.FieldInfo.DeclaringType, field.FieldInfo.FieldType)
                            .GetTypeSyntax();

                    var getterInvoke = SF.CastExpression(
                                getterType,
                                getGetter.Invoke(fieldUtils).AddArgumentListArguments(SF.Argument(fieldInfoField)));

                    body.Add(SF.ExpressionStatement(
                        SF.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SF.IdentifierName(field.GetterFieldName), getterInvoke)));
                }
                if (!field.IsSettableProperty)
                {
                    if (field.FieldInfo.DeclaringType != null && field.FieldInfo.DeclaringType.IsValueType)
                    {
                        var setterType =
                            typeof(ValueTypeSetter<,>).MakeGenericType(
                                field.FieldInfo.DeclaringType,
                                field.FieldInfo.FieldType).GetTypeSyntax();

                        var getValueSetterInvoke = SF.CastExpression(
                                            setterType,
                                            getValueSetter.Invoke(fieldUtils)
                                                .AddArgumentListArguments(SF.Argument(fieldInfoField)));

                        body.Add(SF.ExpressionStatement(
                            SF.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SF.IdentifierName(field.SetterFieldName), getValueSetterInvoke)));
                    }
                    else
                    {
                        var setterType =
                            typeof(Action<,>).MakeGenericType(field.FieldInfo.DeclaringType, field.FieldInfo.FieldType)
                                .GetTypeSyntax();

                        var getReferenceSetterInvoke = SF.CastExpression(
                                            setterType,
                                            getReferenceSetter.Invoke(fieldUtils)
                                                .AddArgumentListArguments(SF.Argument(fieldInfoField)));

                        body.Add(SF.ExpressionStatement(
                            SF.AssignmentExpression(SyntaxKind.SimpleAssignmentExpression, SF.IdentifierName(field.SetterFieldName), getReferenceSetterInvoke)));
                    }
                }

            }

            return
                SF.ConstructorDeclaration(className)
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(fieldUtils.Identifier).WithType(typeof(IFieldUtils).GetTypeSyntax()))
                    .AddBodyStatements(body.ToArray());
        }

        /// <summary>
        /// Returns syntax for the deserializer method.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="fields">The fields.</param>
        /// <returns>Syntax for the deserializer method.</returns>
        private static MemberDeclarationSyntax GenerateDeserializerMethod(Type type, List<FieldInfoMember> fields)
        {
            Expression<Action<IDeserializationContext>> deserializeInner =
                ctx => ctx.DeserializeInner(default(Type));
            var contextParameter = SF.IdentifierName("context");

            var resultDeclaration =
                SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(type.GetTypeSyntax())
                        .AddVariables(
                            SF.VariableDeclarator("result")
                                .WithInitializer(SF.EqualsValueClause(GetObjectCreationExpressionSyntax(type)))));
            var resultVariable = SF.IdentifierName("result");

            var body = new List<StatementSyntax> { resultDeclaration };

            // Value types cannot be referenced, only copied, so there is no need to box & record instances of value types.
            if (!type.IsValueType)
            {
                // Record the result for cyclic deserialization.
                Expression<Action<IDeserializationContext>> recordObject = ctx => ctx.RecordObject(default(object));
                var currentSerializationContext = contextParameter;
                body.Add(
                    SF.ExpressionStatement(
                        recordObject.Invoke(currentSerializationContext)
                            .AddArgumentListArguments(SF.Argument(resultVariable))));
            }

            // Deserialize all fields.
            foreach (var field in fields)
            {
                var deserialized =
                    deserializeInner.Invoke(contextParameter)
                        .AddArgumentListArguments(
                            SF.Argument(SF.TypeOfExpression(field.Type)));
                body.Add(
                    SF.ExpressionStatement(
                        field.GetSetter(
                            resultVariable,
                            SF.CastExpression(field.Type, deserialized))));
            }

            // If the type implements the internal IOnDeserialized lifecycle method, invoke it's method now.
            if (typeof(IOnDeserialized).IsAssignableFrom(type))
            {
                Expression<Action<IOnDeserialized>> onDeserializedMethod = _ => _.OnDeserialized(default(ISerializerContext));

                // C#: ((IOnDeserialized)result).OnDeserialized(context);
                var typedResult = SF.ParenthesizedExpression(SF.CastExpression(typeof(IOnDeserialized).GetTypeSyntax(), resultVariable));
                var invokeOnDeserialized = onDeserializedMethod.Invoke(typedResult).AddArgumentListArguments(SF.Argument(contextParameter));
                body.Add(SF.ExpressionStatement(invokeOnDeserialized));
            }

            body.Add(SF.ReturnStatement(SF.CastExpression(type.GetTypeSyntax(), resultVariable)));
            return
                SF.MethodDeclaration(typeof(object).GetTypeSyntax(), "Deserializer")
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(SF.Identifier("expected")).WithType(typeof(Type).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("context")).WithType(typeof(IDeserializationContext).GetTypeSyntax()))
                    .AddBodyStatements(body.ToArray())
                    .AddAttributeLists(
                        SF.AttributeList()
                            .AddAttributes(SF.Attribute(typeof(DeserializerMethodAttribute).GetNameSyntax())));
        }

        private static MemberDeclarationSyntax GenerateSerializerMethod(Type type, List<FieldInfoMember> fields)
        {
            Expression<Action<ISerializationContext>> serializeInner =
                ctx =>
                    ctx.SerializeInner(default(object), default(Type));
            var contextParameter = SF.IdentifierName("context");

            var body = new List<StatementSyntax>
            {
                SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(type.GetTypeSyntax())
                        .AddVariables(
                            SF.VariableDeclarator("input")
                                .WithInitializer(
                                    SF.EqualsValueClause(
                                        SF.CastExpression(type.GetTypeSyntax(), SF.IdentifierName("untypedInput"))))))
            };

            var inputExpression = SF.IdentifierName("input");

            // Serialize all members.
            foreach (var field in fields)
            {
                body.Add(
                    SF.ExpressionStatement(
                        serializeInner.Invoke(contextParameter)
                            .AddArgumentListArguments(
                                SF.Argument(field.GetGetter(inputExpression, forceAvoidCopy: true)),
                                SF.Argument(SF.TypeOfExpression(field.FieldInfo.FieldType.GetTypeSyntax())))));
            }

            return
                SF.MethodDeclaration(typeof(void).GetTypeSyntax(), "Serializer")
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(SF.Identifier("untypedInput")).WithType(typeof(object).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("context")).WithType(typeof(ISerializationContext).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("expected")).WithType(typeof(Type).GetTypeSyntax()))
                    .AddBodyStatements(body.ToArray())
                    .AddAttributeLists(
                        SF.AttributeList()
                            .AddAttributes(SF.Attribute(typeof(SerializerMethodAttribute).GetNameSyntax())));
        }

        /// <summary>
        /// Returns syntax for the deep copier method.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <param name="fields">The fields.</param>
        /// <returns>Syntax for the deep copier method.</returns>
        private static MemberDeclarationSyntax GenerateDeepCopierMethod(Type type, List<FieldInfoMember> fields)
        {
            var originalVariable = SF.IdentifierName("original");
            var inputVariable = SF.IdentifierName("input");
            var resultVariable = SF.IdentifierName("result");

            var body = new List<StatementSyntax>();
            if (type.GetCustomAttribute<ImmutableAttribute>() != null)
            {
                // Immutable types do not require copying.
                var typeName = type.GetParseableName(new TypeFormattingOptions(includeGlobal: false));
                var comment = SF.Comment($"// No deep copy required since {typeName} is marked with the [Immutable] attribute.");
                body.Add(SF.ReturnStatement(originalVariable).WithLeadingTrivia(comment));
            }
            else
            {
                body.Add(
                    SF.LocalDeclarationStatement(
                        SF.VariableDeclaration(type.GetTypeSyntax())
                            .AddVariables(
                                SF.VariableDeclarator("input")
                                    .WithInitializer(
                                        SF.EqualsValueClause(
                                            SF.ParenthesizedExpression(
                                                SF.CastExpression(type.GetTypeSyntax(), originalVariable)))))));
                body.Add(
                    SF.LocalDeclarationStatement(
                        SF.VariableDeclaration(type.GetTypeSyntax())
                            .AddVariables(
                                SF.VariableDeclarator("result")
                                    .WithInitializer(SF.EqualsValueClause(GetObjectCreationExpressionSyntax(type))))));

                // Record this serialization.
                Expression<Action<ICopyContext>> recordObject =
                    ctx => ctx.RecordCopy(default(object), default(object));
                var context = SF.IdentifierName("context");
                body.Add(
                    SF.ExpressionStatement(
                        recordObject.Invoke(context)
                            .AddArgumentListArguments(SF.Argument(originalVariable), SF.Argument(resultVariable))));

                // Copy all members from the input to the result.
                foreach (var field in fields)
                {
                    body.Add(SF.ExpressionStatement(field.GetSetter(resultVariable, field.GetGetter(inputVariable, context))));
                }

                body.Add(SF.ReturnStatement(resultVariable));
            }

            return
                SF.MethodDeclaration(typeof(object).GetTypeSyntax(), "DeepCopier")
                  .AddModifiers(SF.Token(SyntaxKind.PublicKeyword))
                  .AddParameterListParameters(
                      SF.Parameter(SF.Identifier("original")).WithType(typeof(object).GetTypeSyntax()),
                      SF.Parameter(SF.Identifier("context")).WithType(typeof(ICopyContext).GetTypeSyntax()))
                  .AddBodyStatements(body.ToArray())
                  .AddAttributeLists(
                      SF.AttributeList().AddAttributes(SF.Attribute(typeof(CopierMethodAttribute).GetNameSyntax())));
        }

        /// <summary>
        /// Returns syntax for the static fields of the serializer class.
        /// </summary>
        /// <param name="fields">The fields.</param>
        /// <returns>Syntax for the static fields of the serializer class.</returns>
        private static MemberDeclarationSyntax[] GenerateFields(List<FieldInfoMember> fields)
        {
            var result = new List<MemberDeclarationSyntax>();

            // Add each field and initialize it.
            foreach (var field in fields)
            {
                // Declare the getter for this field.
                if (!field.IsGettableProperty)
                {
                    var getterType =
                        typeof(Func<,>).MakeGenericType(field.FieldInfo.DeclaringType, field.FieldInfo.FieldType)
                            .GetTypeSyntax();
                    var fieldGetterVariable = SF.VariableDeclarator(field.GetterFieldName);

                    result.Add(
                        SF.FieldDeclaration(SF.VariableDeclaration(getterType).AddVariables(fieldGetterVariable))
                            .AddModifiers(
                                SF.Token(SyntaxKind.PrivateKeyword),
                                SF.Token(SyntaxKind.ReadOnlyKeyword)));
                }

                if (!field.IsSettableProperty)
                {
                    if (field.FieldInfo.DeclaringType != null && field.FieldInfo.DeclaringType.IsValueType)
                    {
                        var setterType =
                            typeof(ValueTypeSetter<,>).MakeGenericType(
                                field.FieldInfo.DeclaringType,
                                field.FieldInfo.FieldType).GetTypeSyntax();

                        var fieldSetterVariable = SF.VariableDeclarator(field.SetterFieldName);

                        result.Add(
                            SF.FieldDeclaration(SF.VariableDeclaration(setterType).AddVariables(fieldSetterVariable))
                                .AddModifiers(
                                    SF.Token(SyntaxKind.PrivateKeyword),
                                    SF.Token(SyntaxKind.ReadOnlyKeyword)));
                    }
                    else
                    {
                        var setterType =
                            typeof(Action<,>).MakeGenericType(field.FieldInfo.DeclaringType, field.FieldInfo.FieldType)
                                .GetTypeSyntax();

                        var fieldSetterVariable = SF.VariableDeclarator(field.SetterFieldName);

                        result.Add(
                            SF.FieldDeclaration(SF.VariableDeclaration(setterType).AddVariables(fieldSetterVariable))
                                .AddModifiers(
                                    SF.Token(SyntaxKind.PrivateKeyword),
                                    SF.Token(SyntaxKind.ReadOnlyKeyword)));
                    }
                }
            }

            return result.ToArray();
        }


        /// <summary>
        /// Returns syntax for initializing a new instance of the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>Syntax for initializing a new instance of the provided type.</returns>
        private static ExpressionSyntax GetObjectCreationExpressionSyntax(Type type)
        {
            ExpressionSyntax result;

            if (type.IsValueType)
            {
                // Use the default value.
                result = SF.DefaultExpression(type.GetTypeSyntax());
            }
            else if (GetEmptyConstructor(type) != null)
            {
                // Use the default constructor.
                result = SF.ObjectCreationExpression(type.GetTypeSyntax()).AddArgumentListArguments();
            }
            else
            {
                // Create an unformatted object.
                Expression<Func<object>> getUninitializedObject =
                    () => FormatterServices.GetUninitializedObject(default(Type));

                result = SF.CastExpression(
                    type.GetTypeSyntax(),
                    getUninitializedObject.Invoke()
                        .AddArgumentListArguments(
                            SF.Argument(SF.TypeOfExpression(type.GetTypeSyntax()))));
            }

            return result;
        }

        /// <summary>
        /// Return a parameterless ctor if found. Since typeInfo.GetConstructor can throw BadImageFormatException, 
        /// we have to do the manual filtering here.
        /// </summary>
        /// <param name="typeInfo">The typeInfo</param>
        /// <returns>Given ctor or null if not found.</returns>
        private static ConstructorInfo GetEmptyConstructor(Type typeInfo)
        {
            var result = default(ConstructorInfo);
            var ctors = typeInfo.GetConstructors();

            foreach (var ctor in ctors)
            {
                try
                {
                    if (ctor.GetParameters().Length==0)
                    {
                        result = ctor;
                        break;
                    }
                }
                catch (BadImageFormatException)
                {
                    // Intentionally left blank
                }
            }

            return result;
        }

        /// <summary>
        /// Returns a sorted list of the fields of the provided type.
        /// </summary>
        /// <param name="type">The type.</param>
        /// <returns>A sorted list of the fields of the provided type.</returns>
        private static List<FieldInfoMember> GetFields(Type type)
        {
            var result =
                type.GetAllFields()
                    .Where(ShouldSerializeField)
                    .Select((info, i) => new FieldInfoMember(type, info, i))
                    .ToList();
            result.Sort(FieldInfoMember.Comparer.Instance);
            return result;
        }

        /// <summary>
        /// Returns <see langowrd="true"/> if the provided field should be serialized, <see langword="false"/> otherwise.
        /// </summary>
        /// <param name="field">The field.</param>
        /// <returns>
        /// <see langowrd="true"/> if the provided field should be serialized, <see langword="false"/> otherwise.
        /// </returns>
        private static bool ShouldSerializeField(FieldInfo field)
        {
            if (field.IsNotSerialized()) return false;

            var fieldType = field.GetType();
            if (fieldType.IsPointer || fieldType.IsByRef) return false;

            var handle = fieldType.TypeHandle;
            if (handle.Equals(IntPtrTypeHandle)) return false;
            if (handle.Equals(UIntPtrTypeHandle)) return false;

            if (DelegateType.IsAssignableFrom(fieldType)) return false;
            if (field.DeclaringType?.TypeHandle == MarshalByRefObjectType) return false;

            return true;
        }

        /// <summary>
        /// Represents a field.
        /// </summary>
        private class FieldInfoMember
        {
            private PropertyInfo property;
            private readonly Type targetType;

            /// <summary>
            /// The ordinal assigned to this field.
            /// </summary>
            private readonly int ordinal;

            public FieldInfoMember(Type targetType, FieldInfo field, int ordinal)
            {
                this.targetType = targetType;
                this.FieldInfo = field;
                this.ordinal = ordinal;
            }

            /// <summary>
            /// Gets or sets the underlying <see cref="FieldInfo"/> instance.
            /// </summary>
            public FieldInfo FieldInfo { get; set; }

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
            public bool IsGettableProperty => this.PropertyInfo?.GetGetMethod()?.IsPublic == true && !this.IsObsolete;

            /// <summary>
            /// Gets a value indicating whether or not this field represents a property with an accessible, non-obsolete setter. 
            /// </summary>
            public bool IsSettableProperty => this.PropertyInfo?.GetSetMethod()?.IsPublic == true && !this.IsObsolete;

            /// <summary>
            /// Gets syntax representing the type of this field.
            /// </summary>
            public TypeSyntax Type => this.FieldInfo.FieldType.GetTypeSyntax();

            /// <summary>
            /// Gets the <see cref="PropertyInfo"/> which this field is the backing property for, or
            /// <see langword="null" /> if this is not the backing field of an auto-property.
            /// </summary>
            private PropertyInfo PropertyInfo
            {
                get
                {
                    if (this.property != null)
                    {
                        return this.property;
                    }

                    var propertyName = Regex.Match(this.FieldInfo.Name, "^<([^>]+)>.*$");
                    if (!propertyName.Success || this.FieldInfo.DeclaringType == null) return null;

                    var name = propertyName.Groups[1].Value;
                    var candidates = this.targetType
                        .GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                        .Where(p => string.Equals(name, p.Name, StringComparison.Ordinal))
                        .ToList();

                    if (candidates.Count != 1) return null;
                    var candidate = candidates[0];

                    if (candidate?.PropertyType != this.FieldInfo.FieldType) return null;

                    return this.property = candidate;
                }
            }

            /// <summary>
            /// Gets a value indicating whether or not this field is obsolete.
            /// </summary>
            private bool IsObsolete
            {
                get
                {
                    var obsoleteAttr = this.FieldInfo.GetCustomAttribute<ObsoleteAttribute>();

                    // Get the attribute from the property, if present.
                    if (this.property != null && obsoleteAttr == null)
                    {
                        obsoleteAttr = this.property.GetCustomAttribute<ObsoleteAttribute>();
                    }
                    
                    return obsoleteAttr != null;
                }
            }

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
                if (forceAvoidCopy || this.FieldInfo.FieldType.IsOrleansShallowCopyable())
                {
                    // Return the value without deep-copying it.
                    return getValueExpression;
                }

                // Addressable arguments must be converted to references before passing.
                // IGrainObserver instances cannot be directly converted to references, therefore they are not included.
                ExpressionSyntax deepCopyValueExpression;
                if (typeof(IAddressable).IsAssignableFrom(this.FieldInfo.FieldType)
                    && this.FieldInfo.FieldType.IsInterface
                    && !typeof(IGrainObserver).IsAssignableFrom(this.FieldInfo.FieldType))
                {
                    var getAsReference = getValueExpression.Member(
                        (IAddressable grain) => grain.AsReference<IGrain>(),
                        this.FieldInfo.FieldType);

                    // If the value is not a GrainReference, convert it to a strongly-typed GrainReference.
                    // C#: (value == null || value is GrainReference) ? value : value.AsReference<TInterface>()
                    deepCopyValueExpression =
                        SF.ConditionalExpression(
                            SF.ParenthesizedExpression(
                                SF.BinaryExpression(
                                    SyntaxKind.LogicalOrExpression,
                                    SF.BinaryExpression(
                                        SyntaxKind.EqualsExpression,
                                        getValueExpression,
                                        SF.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                                    SF.BinaryExpression(
                                        SyntaxKind.IsExpression,
                                        getValueExpression,
                                        typeof(GrainReference).GetTypeSyntax()))),
                            getValueExpression,
                            SF.InvocationExpression(getAsReference));
                }
                else
                {
                    deepCopyValueExpression = getValueExpression;
                }

                // Deep-copy the value.
                Expression<Action<ICopyContext>> deepCopyInner = ctx => ctx.DeepCopyInner(default(object));
                var typeSyntax = this.FieldInfo.FieldType.GetTypeSyntax();
                return SF.CastExpression(
                    typeSyntax,
                    deepCopyInner.Invoke(serializationContextExpression)
                                 .AddArgumentListArguments(
                                     SF.Argument(deepCopyValueExpression)));
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
                    return SF.AssignmentExpression(
                        SyntaxKind.SimpleAssignmentExpression,
                        instance.Member(this.PropertyInfo.Name),
                        value);
                }

                var instanceArg = SF.Argument(instance);
                if (this.FieldInfo.DeclaringType != null && this.FieldInfo.DeclaringType.IsValueType)
                {
                    instanceArg = instanceArg.WithRefOrOutKeyword(SF.Token(SyntaxKind.RefKeyword));
                }

                return
                    SF.InvocationExpression(SF.IdentifierName(this.SetterFieldName))
                        .AddArgumentListArguments(instanceArg, SF.Argument(value));
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
                    result = instance.Member(this.PropertyInfo.Name);
                }
                else
                {
                    // Retrieve the field using the generated getter.
                    result =
                        SF.InvocationExpression(SF.IdentifierName(this.GetterFieldName))
                            .AddArgumentListArguments(SF.Argument(instance));
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
                    return string.Compare(x.FieldInfo.Name, y.FieldInfo.Name, StringComparison.Ordinal);
                }
            }
        }
    }
}
