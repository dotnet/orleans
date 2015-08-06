/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

namespace Orleans.CodeGenerator
{
    using System;
    using System.CodeDom.Compiler;
    using System.Collections;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Reflection;

    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using Orleans;
    using Orleans.Async;
    using Orleans.CodeGeneration;
    using Orleans.CodeGenerator.Utilities;
    using Orleans.Runtime;
    using Orleans.Serialization;

    using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;
    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    /// <summary>
    /// Code generator which generates grain state classes.
    /// </summary>
    public static class GrainStateGenerator
    {
        /// <summary>
        /// The suffix appended to the name of generated classes.
        /// </summary>
        private const string ClassSuffix = "GrainState";

        /// <summary>
        /// The compiled assemblies.
        /// </summary>
        private static readonly ConcurrentDictionary<Assembly, Tuple<Assembly, string>> CompiledAssemblies =
            new ConcurrentDictionary<Assembly, Tuple<Assembly, string>>();

        /// <summary>
        /// The mapping between known interface types and a suitable implementation for that interface.
        /// </summary>
        private static readonly Dictionary<Type, Type> InterfaceImplementationMap = new Dictionary<Type, Type>
        {
            { typeof(IList<>), typeof(List<>) },
            { typeof(ICollection<>), typeof(List<>) },
            { typeof(IEnumerable<>), typeof(List<>) },
            { typeof(ILookup<,>), typeof(Dictionary<,>) },
            { typeof(IDictionary<,>), typeof(Dictionary<,>) },
            { typeof(ISet<>), typeof(HashSet<>) },
            { typeof(IEnumerable), typeof(List<object>) },
            { typeof(IList), typeof(List<object>) },
            { typeof(ICollection), typeof(List<object>) },
            { typeof(IDictionary), typeof(Dictionary<object,object>) },
        };

        /// <summary>
        /// Creates corresponding grain state classes for all currently loaded grain types.
        /// </summary>
        public static void CreateForCurrentlyLoadedAssemblies()
        {
            var assemblies = AppDomain.CurrentDomain.GetAssemblies().Where(_ => !_.IsDynamic).ToList();
            if (assemblies.Count == CompiledAssemblies.Count)
            {
                // Already up to date.
                return;
            }

            // Generate code for newly loaded assemblies.
            var grainAssemblies =
                assemblies.Where(
                    asm =>
                    !CompiledAssemblies.ContainsKey(asm) && asm.GetCustomAttribute<GeneratedCodeAttribute>() == null
                    && asm.GetTypes().Any(CodeGeneratorCommon.ShouldGenerate));
            foreach (var assembly in grainAssemblies)
            {
                GenerateAndLoadForAssembly(assembly);
            }
        }

        /// <summary>
        /// Returns a generated assembly containing grain state classes for all currently loaded grain types.
        /// </summary>
        /// <param name="assembly">
        ///     The input assembly.
        /// </param>
        /// <returns>
        /// A generated assembly containing grain state classes for all currently loaded grain types.
        /// </returns>
        public static void GenerateAndLoadForAssembly(Assembly assembly)
        {
            if (assembly.IsDynamic)
            {
                return;
            }

            CompiledAssemblies.GetOrAdd(assembly, GenerateForAssembly);
        }

        private static Tuple<Assembly, string> GenerateForAssembly(Assembly assembly)
        {
            var grainTypes = CodeGeneratorCommon.GetGrainImplementations(assembly, filter: IsStatefulGrain);

            if (grainTypes.Count <= 0)
            {
                return Tuple.Create(default(Assembly), string.Empty);
            }

            string source;
            var syntaxTree = GenerateCompilationUnit(grainTypes);
            var compiled = CodeGeneratorCommon.CompileAssembly(syntaxTree, assembly.GetName().Name + "_" + ClassSuffix + ".dll", out source);
            
            return Tuple.Create(compiled, source);
        }

        public static string GenerateSourceForAssembly(Assembly assembly)
        {
            var grainTypes = CodeGeneratorCommon.GetGrainImplementations(
                assembly,
                /*CodeGeneratorCommon.GetTypesWithImplementations<GrainStateAttribute>(),*/
                filter: IsStatefulGrain);
            return CodeGeneratorCommon.GenerateSourceCode(GenerateCompilationUnit(grainTypes));
        }

        private static bool IsStatefulGrain(Type grainType)
        {
            var stateType = GetGrainStateType(grainType);
            return stateType != null && !typeof(GrainState).IsAssignableFrom(stateType);
        }

        /// <summary>
        /// Returns the state type of the provided <paramref name="grainType"/> or <see langword="null"/> if
        /// <paramref name="grainType"/> is stateless.
        /// </summary>
        /// <param name="grainType">The grain type.</param>
        /// <returns>
        /// The state type of the provided <paramref name="grainType"/> or <see langword="null"/> if
        /// <paramref name="grainType"/> is stateless.
        /// </returns>
        private static Type GetGrainStateType(Type grainType)
        {
            if (!typeof(Grain).IsAssignableFrom(grainType))
            {
                return null;
            }
            
            // Traverse through the class hierarchy to find the Grain<TGrainState> class.
            for (var current = grainType; current != null; current = current.BaseType)
            {
                var parent = current.BaseType;
                if (parent != null && parent.IsConstructedGenericType
                    && parent.GetGenericTypeDefinition() == typeof(Grain<>))
                {
                    // Extract TGrainState from Grain<TGrainState>.
                    return parent.GetGenericArguments()[0];
                }
            }

            return null;
        }

        /// <summary>
        /// Returns compilation unit syntax for dispatching events to the provided <paramref name="grainTypes"/>.
        /// </summary>
        /// <param name="grainTypes">
        /// The grain types.
        /// </param>
        /// <returns>
        /// Compilation unit syntax for dispatching events to the provided <paramref name="grainTypes"/>.
        /// </returns>
        [SuppressMessage("ReSharper", "CoVariantArrayConversion", Justification = "Array is never mutated.")]
        private static CompilationUnitSyntax GenerateCompilationUnit(IList<Type> grainTypes)
        {
            var usings =
                TypeUtils.GetNamespaces(typeof(TaskUtility), typeof(GrainExtensions))
                    .Select(_ => SF.UsingDirective(SF.ParseName(_)))
                    .ToArray();

            var members = new List<MemberDeclarationSyntax>();
            foreach (var group in grainTypes.GroupBy(_ => CodeGeneratorCommon.GetGeneratedNamespace(_)))
            {
                members.Add(
                    SF.NamespaceDeclaration(SF.ParseName(group.Key))
                        .AddUsings(usings)
                        .AddMembers(group.Select(GenerateClass).ToArray()));
            }

            return SF.CompilationUnit().AddMembers(members.ToArray());
        }

        /// <summary>
        /// Generates the class for the provided grain types.
        /// </summary>
        /// <param name="grainType">
        /// The grain interface type.
        /// </param>
        /// <returns>
        /// The generated class.
        /// </returns>
        private static TypeDeclarationSyntax GenerateClass(Type grainType)
        {
            var stateType = GetGrainStateType(grainType);
            if (stateType == null)
            {
                throw new ArgumentException("Grain is not stateful or state type not found.", "grainType");
            }

            var genericTypes = grainType.IsGenericTypeDefinition
                                   ? grainType.GetGenericArguments()
                                   : new Type[0];

            // Create the special marker attribute.
            var attributes = SF.AttributeList()
                .AddAttributes(
                    CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
                    SF.Attribute(typeof(GrainStateAttribute).GetNameSyntax())
                        .AddArgumentListArguments(
                            SF.AttributeArgument(
                                SF.TypeOfExpression(grainType.GetTypeSyntax(includeGenericParameters: false)))));
            var className = CodeGeneratorCommon.ClassPrefix + TypeUtils.GetSuitableClassName(stateType) + ClassSuffix;
            var classTypeSyntax = GetGrainStateClassName(className, genericTypes);
            var classDeclaration =
                SF.ClassDeclaration(className)
                    .AddModifiers(SF.Token(SyntaxKind.InternalKeyword))
                    .AddBaseListTypes(
                        SF.SimpleBaseType(typeof(GrainState).GetTypeSyntax()),
                        SF.SimpleBaseType(stateType.GetTypeSyntax()))
                    .AddMembers(GenerateConstructors(grainType, className))
                    .AddMembers(GenerateStateProperties(grainType))
                    .AddMembers(GenerateSetAllMethod(grainType))
                    .AddMembers(GenerateAsDictionaryMethod(grainType))
                    .AddMembers(GenerateCopierMethod(classTypeSyntax))
                    .AddMembers(GenerateSerializerMethod(classTypeSyntax))
                    .AddMembers(GenerateDeserializerMethod(classTypeSyntax))
                    .AddAttributeLists(attributes);
            if (genericTypes.Length > 0)
            {
                classDeclaration =
                    classDeclaration.AddTypeParameterListParameters(
                        genericTypes.Select(_ => SF.TypeParameter(_.Name.ToIdentifier())).ToArray());
            }

            return classDeclaration;
        }

        private static MemberDeclarationSyntax[] GenerateStateProperties(Type grainType)
        {
            var stateType = GetGrainStateType(grainType);
            var properties = GrainInterfaceData.GetPersistentProperties(stateType);
            var result = new List<MemberDeclarationSyntax>();
            foreach (var property in properties)
            {
                var declaration =
                    property.GetDeclarationSyntax()
                        .AddModifiers(SF.Token(SyntaxKind.PublicKeyword))
                        .AddAccessorListAccessors(
                            SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                                .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)),
                            SF.AccessorDeclaration(SyntaxKind.SetAccessorDeclaration)
                                .WithSemicolonToken(SF.Token(SyntaxKind.SemicolonToken)));
                result.Add(declaration);
            }

            return result.ToArray();
        }

        private static MemberDeclarationSyntax[] GenerateConstructors(Type grainType, string className)
        {
            var stateType = GetGrainStateType(grainType);

            var baseConstructor =
                typeof(GrainState).GetConstructor(
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[]{typeof(string)},
                    new ParameterModifier[0]);

            var declaration =
                baseConstructor.GetDeclarationSyntax(className)
                    .WithParameterList(SF.ParameterList())
                    .WithInitializer(
                        SF.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer)
                            .AddArgumentListArguments(SF.Argument(stateType.GetParseableName().GetLiteralExpression())))
                    .AddBodyStatements()
                    .WithModifiers(SF.TokenList().Add(SF.Token(SyntaxKind.PublicKeyword)));

            return new MemberDeclarationSyntax[] { declaration };
        }

        private static MemberDeclarationSyntax GenerateSetAllMethod(Type grainType)
        {
            var method = TypeUtils.Method((GrainState _) => _.SetAll(null));
            var values = method.GetParameters()[0].Name.ToIdentifierName();

            var stateType = GetGrainStateType(grainType);
            var properties = GrainInterfaceData.GetPersistentProperties(stateType);
            var current = SF.Identifier("val");
            var valDeclaration = SF.LocalDeclarationStatement(
                SF.VariableDeclaration(typeof(object).GetTypeSyntax())
                    .AddVariables(SF.VariableDeclarator(current)));
            var statements = new List<StatementSyntax>
            {
                valDeclaration,
                SF.IfStatement(
                    SF.BinaryExpression(
                        SyntaxKind.EqualsExpression,
                        values,
                        SF.LiteralExpression(SyntaxKind.NullLiteralExpression)),
                    SF.Block()
                        .AddStatements(GeneratePropertyInitializers(properties))
                        .AddStatements(SF.ReturnStatement()))
            };
            
            // For each property, try to get its value from the state dictionary.
            foreach (var property in properties)
            {
                object ignored;
                var tryGetValue =
                    SF.InvocationExpression(
                        values.Member((Dictionary<string, object> _) => _.TryGetValue(String.Empty, out ignored)))
                        .AddArgumentListArguments(
                            SF.Argument(property.Name.GetLiteralExpression()),
                            SF.Argument(SF.IdentifierName(current)).WithRefOrOutKeyword(SF.Token(SyntaxKind.OutKeyword)));
                statements.Add(
                    SF.IfStatement(
                        tryGetValue,
                        SF.Block(
                            SF.ExpressionStatement(
                                SF.AssignmentExpression(
                                    SyntaxKind.SimpleAssignmentExpression,
                                    SF.ThisExpression().Member(property.Name),
                                    SF.CastExpression(property.PropertyType.GetTypeSyntax(), SF.IdentifierName("val")))))));
            }

            return
                method.GetDeclarationSyntax()
                    .AddBodyStatements(statements.ToArray())
                    .AddModifiers(SF.Token(SyntaxKind.OverrideKeyword));
        }

        private static MemberDeclarationSyntax GenerateAsDictionaryMethod(Type grainType)
        {
            var method = TypeUtils.Method((GrainState _) => _.AsDictionary());

            var stateType = GetGrainStateType(grainType);
            var properties = GrainInterfaceData.GetPersistentProperties(stateType);

            var resultVar =
                SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(typeof(Dictionary<string, object>).GetTypeSyntax())
                        .AddVariables(
                            SF.VariableDeclarator("result")
                                .WithInitializer(
                                    SF.EqualsValueClause(
                                        SF.ObjectCreationExpression(typeof(Dictionary<string, object>).GetTypeSyntax())
                                            .AddArgumentListArguments()))));

            var statements = new List<StatementSyntax> { resultVar };
            foreach (var property in properties)
            {
                statements.Add(
                    SF.ExpressionStatement(
                        SF.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SF.ElementAccessExpression(SF.IdentifierName("result"))
                                .AddArgumentListArguments(SF.Argument(property.Name.GetLiteralExpression())),
                            SF.ThisExpression().Member(property.Name))));
            }

            statements.Add(SF.ReturnStatement(SF.IdentifierName("result")));
            return method.GetDeclarationSyntax()
                .AddModifiers(SF.Token(SyntaxKind.OverrideKeyword))
                .AddBodyStatements(statements.ToArray());
        }

        /// <summary>
        /// Generates statements for initializing a collection of properties.
        /// </summary>
        /// <param name="properties">The properties.</param>
        /// <returns>Statements for initializing a collection of properties.</returns>
        private static StatementSyntax[] GeneratePropertyInitializers(PropertyInfo[] properties)
        {
            var results = new List<StatementSyntax>();
            foreach (var property in properties)
            {
                var concreteType = GetConcreteType(property.PropertyType);

                // Types without a default constructor are initialized to their default value.
                var hasConstructor = concreteType.GetConstructor(Type.EmptyTypes) != null;
                var initializer = hasConstructor
                                     ? (ExpressionSyntax)
                                       SF.ObjectCreationExpression(concreteType.GetTypeSyntax())
                                           .AddArgumentListArguments()
                                     : SF.DefaultExpression(concreteType.GetTypeSyntax());
                results.Add(
                    SF.ExpressionStatement(
                        SF.AssignmentExpression(
                            SyntaxKind.SimpleAssignmentExpression,
                            SF.ThisExpression().Member(property.Name),
                            initializer)));
            }

            return results.ToArray();
        }

        private static Type GetConcreteType(Type type)
        {
            Type concreteType;
            if (type.IsConstructedGenericType)
            {
                var generic = type.GetGenericTypeDefinition();
                if (InterfaceImplementationMap.TryGetValue(generic, out concreteType))
                {
                    return concreteType.MakeGenericType(type.GetGenericArguments());
                }
            }

            if (InterfaceImplementationMap.TryGetValue(type, out concreteType))
            {
                return concreteType;
            }

            return type;
        }

        private static MemberDeclarationSyntax GenerateCopierMethod(TypeSyntax classTypeSyntax)
        {
            var markerAttribute =
                SF.AttributeList().AddAttributes(SF.Attribute(typeof(CopierMethodAttribute).GetNameSyntax()));
            return
                SF.MethodDeclaration(typeof(object).GetTypeSyntax(), "_Copier")
                    .AddAttributeLists(markerAttribute)
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(SF.Identifier("original")).WithType(typeof(object).GetTypeSyntax()))
                    .AddBodyStatements(
                        SF.ReturnStatement(
                            SF.InvocationExpression(
                                SF.ParenthesizedExpression(SF.CastExpression(classTypeSyntax, SF.IdentifierName("original")))
                                    .Member((GrainState _) => _.DeepCopy())).AddArgumentListArguments()));
        }

        private static MemberDeclarationSyntax GenerateSerializerMethod(TypeSyntax classTypeSyntax)
        {
            var markerAttribute =
                SF.AttributeList().AddAttributes(SF.Attribute(typeof(SerializerMethodAttribute).GetNameSyntax()));
            return
                SF.MethodDeclaration(typeof(void).GetTypeSyntax(), "_Serializer")
                    .AddAttributeLists(markerAttribute)
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(SF.Identifier("original")).WithType(typeof(object).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("stream")).WithType(typeof(BinaryTokenStreamWriter).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("expectedType")).WithType(typeof(Type).GetTypeSyntax()))
                    .AddBodyStatements(
                        SF.ExpressionStatement(
                            SF.InvocationExpression(
                                SF.ParenthesizedExpression(
                                    SF.CastExpression(classTypeSyntax, SF.IdentifierName("original")))
                                    .Member((GrainState _) => _.SerializeTo(null)))
                                .AddArgumentListArguments(SF.Argument(SF.IdentifierName("stream")))));
        }

        private static MemberDeclarationSyntax GenerateDeserializerMethod(TypeSyntax classTypeSyntax)
        {
            var markerAttribute =
                SF.AttributeList().AddAttributes(SF.Attribute(typeof(DeserializerMethodAttribute).GetNameSyntax()));
            var resultVariable =
                SF.LocalDeclarationStatement(
                    SF.VariableDeclaration(classTypeSyntax)
                        .AddVariables(
                            SF.VariableDeclarator("result")
                                .WithInitializer(
                                    SF.EqualsValueClause(
                                        SF.ObjectCreationExpression(classTypeSyntax)
                                            .AddArgumentListArguments()))));
            var deserializationStatement =
                SF.ExpressionStatement(
                    SF.InvocationExpression(
                        SF.IdentifierName("result").Member((GrainState _) => _.DeserializeFrom(null)))
                        .AddArgumentListArguments(SF.Argument(SF.IdentifierName("stream"))));
            return
                SF.MethodDeclaration(typeof(object).GetTypeSyntax(), "_Deserializer")
                    .AddAttributeLists(markerAttribute)
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.StaticKeyword))
                    .AddParameterListParameters(
                        SF.Parameter(SF.Identifier("expected")).WithType(typeof(Type).GetTypeSyntax()),
                        SF.Parameter(SF.Identifier("stream")).WithType(typeof(BinaryTokenStreamReader).GetTypeSyntax()))
                    .AddBodyStatements(
                        resultVariable,
                        deserializationStatement,
                        SF.ReturnStatement(SF.IdentifierName("result")));
        }

        private static TypeSyntax GetGrainStateClassName(string className, Type[] genericTypes)
        {
            if (genericTypes.Length > 0)
            {
                return
                    SF.ParseTypeName(
                        string.Format(
                            "{0}<{1}>",
                            className,
                            string.Join(",", genericTypes.Select(_ => _.GetParseableName()))));
            }

            return SF.ParseTypeName(className);
        }
    }
}