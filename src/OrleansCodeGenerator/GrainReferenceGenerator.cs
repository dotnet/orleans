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
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics.CodeAnalysis;
    using System.Linq;
    using System.Linq.Expressions;
    using System.Reflection;
    using System.Runtime.CompilerServices;
    using System.Threading.Tasks;

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
    /// Code generator which generates <see cref="GrainReference"/>s for grains.
    /// </summary>
    public static class GrainReferenceGenerator
    {
        /// <summary>
        /// The suffix appended to the name of generated classes.
        /// </summary>
        private const string ClassSuffix = "Reference";

        /// <summary>
        /// The compiled assemblies.
        /// </summary>
        private static readonly ConcurrentDictionary<Assembly, Tuple<Assembly, string>> CompiledAssemblies =
            new ConcurrentDictionary<Assembly, Tuple<Assembly, string>>();

        /// <summary>
        /// A reference to the CheckGrainObserverParamInternal method.
        /// </summary>
        private static readonly Expression<Action> CheckGrainObserverParamInternalExpression =
            () => GrainFactoryBase.CheckGrainObserverParamInternal(null);

        /// <summary>
        /// Creates corresponding <see cref="GrainReference"/> for all grain types defined in currently loaded
        /// assemblies.
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
        /// Returns generated source code for <see cref="GrainReference"/> implementations for the provided
        /// <paramref name="assembly"/>.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <returns>
        /// Generated source code for <see cref="GrainReference"/> implementations for the provided <paramref name="assembly"/>.
        /// </returns>
        public static string GenerateSourceForAssembly(Assembly assembly)
        {
            var grainTypes = CodeGeneratorCommon.GetGrainInterfaces(assembly);
            return CodeGeneratorCommon.GenerateSourceCode(GenerateCompilationUnit(grainTypes));
        }

        /// <summary>
        /// Returns all generated source code.
        /// </summary>
        /// <returns>All generated source code.</returns>
        public static IEnumerable<string> GetAllSource()
        {
            return CompiledAssemblies.Values.Select(_ => _.Item2);
        }

        /// <summary>
        /// Returns a generated assembly containing <see cref="GrainReference"/>s for the grains defined in
        /// the provided <paramref name="assembly"/>.
        /// </summary>
        /// <param name="assembly">
        /// The grain assembly.
        /// </param>
        /// <returns>
        /// A generated assembly containing <see cref="GrainReference"/>s for the grains defined in the provided
        /// <paramref name="assembly"/>.
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
            var existingReferences = CodeGeneratorCommon.GetTypesWithImplementations<GrainReferenceAttribute>();
            var grainTypes = CodeGeneratorCommon.GetGrainInterfaces(assembly, existingReferences, _ => _.IsPublic);

            if (grainTypes.Count <= 0)
            {
                return Tuple.Create(default(Assembly), string.Empty);
            }

            var syntaxTree = GenerateCompilationUnit(grainTypes);
            string source;
            var compiled = CodeGeneratorCommon.CompileAssembly(syntaxTree, assembly.GetName().Name + "_" + ClassSuffix + ".dll", out source);
            RegisterGrainReferenceSerializers(assembly);
            return Tuple.Create(compiled, source);
        }

        /// <summary>
        /// Registers GrainRefernece serializers for the provided <paramref name="assembly"/>.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        private static void RegisterGrainReferenceSerializers(Assembly assembly)
        {
            foreach (var type in assembly.GetTypes())
            {
                var attr = type.GetCustomAttribute<GrainReferenceAttribute>();
                if (attr == null || attr.GrainType == null)
                {
                    continue;
                }

                // Register GrainReference serialization methods.
                SerializationManager.Register(
                    type,
                    GrainReference.CopyGrainReference,
                    GrainReference.SerializeGrainReference,
                    (expected, stream) =>
                    {
                        var grainType = attr.GrainType;
                        if (expected.IsConstructedGenericType)
                        {
                            grainType = grainType.MakeGenericType(expected.GenericTypeArguments);
                        }

                        var deserialized = (IAddressable)GrainReference.DeserializeGrainReference(expected, stream);
                        return RuntimeClient.Current.InternalGrainFactory.Cast(deserialized, grainType);
                    });
            }
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
                TypeUtils.GetNamespaces( typeof(TaskUtility), typeof(GrainExtensions))
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
            var genericTypes = grainType.IsGenericTypeDefinition
                                   ? grainType.GetGenericArguments()
                                         .Select(_ => SF.TypeParameter(_.ToString()))
                                         .ToArray()
                                   : new TypeParameterSyntax[0];
            
            // Create the special marker attribute.
            var markerAttribute =
                SF.Attribute(typeof(GrainReferenceAttribute).GetNameSyntax())
                    .AddArgumentListArguments(
                        SF.AttributeArgument(
                            SF.TypeOfExpression(grainType.GetTypeSyntax(includeGenericParameters: false))));
            var attributes = SF.AttributeList()
                .AddAttributes(
                    CodeGeneratorCommon.GetGeneratedCodeAttributeSyntax(),
                    SF.Attribute(typeof(SerializableAttribute).GetNameSyntax()),
                    markerAttribute);

            var className = CodeGeneratorCommon.ClassPrefix + TypeUtils.GetSuitableClassName(grainType) + ClassSuffix;
            var classDeclaration =
                SF.ClassDeclaration(className)
                    .AddModifiers(SF.Token(SyntaxKind.InternalKeyword))
                    .AddBaseListTypes(
                        SF.SimpleBaseType(typeof(GrainReference).GetTypeSyntax()),
                        SF.SimpleBaseType(grainType.GetTypeSyntax()))
                    .AddMembers(GenerateConstructors(className))
                    .AddMembers(
                        GenerateInterfaceIdProperty(grainType),
                        GenerateInterfaceNameProperty(grainType),
                        GenerateIsCompatibleMethod(grainType),
                        GenerateGetMethodNameMethod(grainType))
                    .AddMembers(GenerateInvokeMethods(grainType))
                    .AddAttributeLists(attributes);
            if (genericTypes.Length > 0)
            {
                classDeclaration = classDeclaration.AddTypeParameterListParameters(genericTypes);
            }

            return classDeclaration;
        }

        private static MemberDeclarationSyntax[] GenerateConstructors(string className)
        {
            var baseConstructors =
                typeof(GrainReference).GetConstructors(
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Instance).Where(_ => !_.IsPrivate);
            var constructors = new List<MemberDeclarationSyntax>();
            foreach (var baseConstructor in baseConstructors)
            {
                var args = baseConstructor.GetParameters()
                    .Select(arg => SF.Argument(arg.Name.ToIdentifierName()))
                    .ToArray();
                var declaration =
                    baseConstructor.GetDeclarationSyntax(className)
                        .WithInitializer(
                            SF.ConstructorInitializer(SyntaxKind.BaseConstructorInitializer)
                                .AddArgumentListArguments(args))
                        .AddBodyStatements();
                constructors.Add(declaration);
            }

            return constructors.ToArray();
        }

        private static MemberDeclarationSyntax[] GenerateInvokeMethods(Type grainType)
        {
            var baseReference = SF.BaseExpression();
            var methods = GrainInterfaceData.GetMethods(grainType);
            var members = new List<MemberDeclarationSyntax>();
            foreach (var method in methods)
            {
                var methodId = GrainInterfaceData.ComputeMethodId(method);
                var methodIdArgument =
                    SF.Argument(SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(methodId)));

                // Construct a new object array from all method arguments.
                var parameters = method.GetParameters();
                var body = new List<StatementSyntax>();
                foreach (var parameter in parameters)
                {
                    if (typeof(IGrainObserver).IsAssignableFrom(parameter.ParameterType))
                    {
                        body.Add(
                            SF.ExpressionStatement(
                                CheckGrainObserverParamInternalExpression.Invoke()
                                    .AddArgumentListArguments(SF.Argument(parameter.Name.ToIdentifierName()))));
                    }
                }

                var args = SF.ArrayCreationExpression(typeof(object).GetArrayTypeSyntax())
                    .WithInitializer(
                        SF.InitializerExpression(SyntaxKind.ArrayInitializerExpression)
                            .AddExpressions(
                                parameters
                                    .Select(GetParameterForInvocation)
                                    .ToArray()));

                if (method.ReturnType == typeof(void))
                {
                    body.Add(
                        SF.ExpressionStatement(
                            SF.InvocationExpression(baseReference.Member("InvokeOneWayMethod"))
                                .AddArgumentListArguments(methodIdArgument)
                                .AddArgumentListArguments(SF.Argument(args))));
                }
                else
                {
                    var returnType = method.ReturnType == typeof(Task)
                                         ? typeof(object)
                                         : method.ReturnType.GenericTypeArguments[0];
                    body.Add(
                        SF.ReturnStatement(
                            SF.InvocationExpression(baseReference.Member("InvokeMethodAsync", returnType))
                                .AddArgumentListArguments(methodIdArgument)
                                .AddArgumentListArguments(SF.Argument(args))));
                }

                members.Add(method.GetDeclarationSyntax().AddBodyStatements(body.ToArray()));
            }

            return members.ToArray();
        }

        private static ExpressionSyntax GetParameterForInvocation(ParameterInfo arg)
        {
            var argIdentifier = arg.Name.ToIdentifierName();

            // Addressable arguments must be converted to references before passing.
            if (typeof(IAddressable).IsAssignableFrom(arg.ParameterType)
                && (typeof(Grain).IsAssignableFrom(arg.ParameterType) || arg.ParameterType.IsInterface))
            {
                return
                    SF.ConditionalExpression(
                        SF.BinaryExpression(SyntaxKind.IsExpression, argIdentifier, typeof(Grain).GetTypeSyntax()),
                        SF.InvocationExpression(argIdentifier.Member("AsReference", arg.ParameterType)),
                        argIdentifier);
            }

            return argIdentifier;
        }

        private static MemberDeclarationSyntax GenerateInterfaceIdProperty(Type grainType)
        {
            var property = TypeUtils.Member((IGrainMethodInvoker _) => _.InterfaceId);
            var returnValue = SF.LiteralExpression(
                SyntaxKind.NumericLiteralExpression,
                SF.Literal(GrainInterfaceData.GetGrainInterfaceId(grainType)));
            return
                SF.PropertyDeclaration(typeof(int).GetTypeSyntax(), property.Name)
                    .AddAccessorListAccessors(
                        SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .AddBodyStatements(SF.ReturnStatement(returnValue)))
                    .AddModifiers(SF.Token(SyntaxKind.ProtectedKeyword), SF.Token(SyntaxKind.OverrideKeyword));
        }

        private static MemberDeclarationSyntax GenerateIsCompatibleMethod(Type grainType)
        {
            var method = TypeUtils.Method((GrainReference _) => _.IsCompatible(default(int)));
            var methodDeclaration = method.GetDeclarationSyntax();
            var interfaceIdParameter = method.GetParameters()[0].Name.ToIdentifierName();

            var interfaceIds =
                new HashSet<int>(
                    new[] { GrainInterfaceData.GetGrainInterfaceId(grainType) }.Concat(
                        GrainInterfaceData.GetRemoteInterfaces(grainType).Keys));

            var returnValue = default(BinaryExpressionSyntax);
            foreach (var interfaceId in interfaceIds)
            {
                var check = SF.BinaryExpression(
                    SyntaxKind.EqualsExpression,
                    interfaceIdParameter,
                    SF.LiteralExpression(SyntaxKind.NumericLiteralExpression, SF.Literal(interfaceId)));

                // If this is the first check, assign it, otherwise OR this check with the previous checks.
                returnValue = returnValue == null
                                  ? check
                                  : SF.BinaryExpression(SyntaxKind.LogicalOrExpression, returnValue, check);
            }

            return
                methodDeclaration.AddBodyStatements(SF.ReturnStatement(returnValue))
                    .AddModifiers(SF.Token(SyntaxKind.OverrideKeyword));
        }

        private static MemberDeclarationSyntax GenerateInterfaceNameProperty(Type grainType)
        {
            var propertyName = TypeUtils.Member((GrainReference _) => _.InterfaceName);
            var returnValue = grainType.GetParseableName().GetLiteralExpression();
            return
                SF.PropertyDeclaration(typeof(string).GetTypeSyntax(), propertyName.Name)
                    .AddAccessorListAccessors(
                        SF.AccessorDeclaration(SyntaxKind.GetAccessorDeclaration)
                            .AddBodyStatements(SF.ReturnStatement(returnValue)))
                    .AddModifiers(SF.Token(SyntaxKind.PublicKeyword), SF.Token(SyntaxKind.OverrideKeyword));
        }

        private static MethodDeclarationSyntax GenerateGetMethodNameMethod(Type grainType)
        {
            // Get the method with the correct type.
            var method = typeof(GrainReference).GetMethod(
                "GetMethodName",
                BindingFlags.NonPublic | BindingFlags.Instance);

            var methodDeclaration =
                method.GetDeclarationSyntax()
                    .AddModifiers(SF.Token(SyntaxKind.OverrideKeyword));
            var parameters = method.GetParameters();

            var interfaceIdArgument = parameters[0].Name.ToIdentifierName();
            var methodIdArgument = parameters[1].Name.ToIdentifierName();

            var interfaceCases = CodeGeneratorCommon.GenerateGrainInterfaceAndMethodSwitch(
                grainType,
                methodIdArgument,
                methodType => new StatementSyntax[] { SF.ReturnStatement(methodType.Name.GetLiteralExpression()) });

            // Generate the default case, which will throw a NotImplementedException.
            var errorMessage = SF.BinaryExpression(
                SyntaxKind.AddExpression,
                "interfaceId=".GetLiteralExpression(),
                interfaceIdArgument);
            var throwStatement =
                SF.ThrowStatement(
                    SF.ObjectCreationExpression(typeof(NotImplementedException).GetTypeSyntax())
                        .AddArgumentListArguments(SF.Argument(errorMessage)));
            var defaultCase = SF.SwitchSection().AddLabels(SF.DefaultSwitchLabel()).AddStatements(throwStatement);
            var interfaceIdSwitch =
                SF.SwitchStatement(interfaceIdArgument).AddSections(interfaceCases.ToArray()).AddSections(defaultCase);

            return methodDeclaration.AddBodyStatements(interfaceIdSwitch);
        }
    }
}