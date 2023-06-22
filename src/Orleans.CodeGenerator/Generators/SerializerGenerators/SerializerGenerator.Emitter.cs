namespace Orleans.CodeGenerator.Generators.SerializerGenerators;

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

internal partial class SerializerGenerator
{
    private class Emitter : EmitterBase
    {

        private static string _metadataClassName;
        private static string _metadataClassNamespace;
        private static SerializerGeneratorContext _context;
        private static SyntaxList<UsingDirectiveSyntax> _usings = List(new[] { UsingDirective(ParseName("global::Orleans.Serialization.Codecs")), UsingDirective(ParseName("global::Orleans.Serialization.GeneratedCodeHelpers")) });

        private static MetadataModel _metadataModel;

        public Emitter(SerializerGeneratorContext context, SourceProductionContext sourceProductionContext) : base(sourceProductionContext)
        {
            _context = context;
            _metadataModel = new()
            {
                CompoundTypeAliases = _context.CompoundTypeAliases,
                DefaultCopiers = _context.DefaultCopiers,
                DetectedActivators = _context.DetectedActivators,
                DetectedConverters = _context.DetectedConverters,
                DetectedCopiers = _context.DetectedCopiers,
                DetectedSerializers = _context.DetectedSerializers,
                GeneratedInvokables = _context.GeneratedInvokables,
                GeneratedProxies = _context.GeneratedProxies,
                InvokableInterfaceImplementations = _context.InvokableInterfaceImplementations,
                InvokableInterfaces = _context.InvokableInterfaces,
                SerializableTypes = _context.SerializableTypes,
                ActivatableTypes = _context.ActivatableTypes,

            };
        }

        private static CompilationUnitSyntax GetCompilationUnit(SyntaxList<UsingDirectiveSyntax>? usings = default, params MemberDeclarationSyntax[] namespaces)
        {
            var cus = CompilationUnit().WithMembers(List(namespaces));
            if (usings != null)
                cus.WithUsings(usings.Value);
            return cus;
        }

        private static NamespaceDeclarationSyntax GetNamespaceDeclarationSyntax(string namespaceName, SyntaxList<UsingDirectiveSyntax>? usings = default, params MemberDeclarationSyntax[] memberDeclarationSyntaxes)
        {
            var nds = NamespaceDeclaration(ParseName(namespaceName));
            if (usings is not null)
                nds = nds.WithUsings(usings.Value);
            if (memberDeclarationSyntaxes.Any())
                nds = nds.WithMembers(List(memberDeclarationSyntaxes));

            return nds;
        }

        private static ClassDeclarationSyntax GetClassDeclarationSyntax(params MemberDeclarationSyntax[] classMembers)
        {
            return ClassDeclaration(_metadataClassName + "_TypeManifestOptionsExtensionMethods")
                .AddModifiers(Token(SyntaxKind.InternalKeyword), Token(SyntaxKind.StaticKeyword), Token(SyntaxKind.PartialKeyword))
                .AddMembers(classMembers);
        }

        private static MethodDeclarationSyntax GetMethodDeclarationSyntax()
        {
            IdentifierNameSyntax configParam = "config".ToIdentifierName();
            List<StatementSyntax> body = GetStatementSyntaxes(configParam);
            return MethodDeclaration(PredefinedType(Token(SyntaxKind.VoidKeyword)), "AddSerializerTypesFromAssembly")
                .AddModifiers(Token(SyntaxKind.PublicKeyword), Token(SyntaxKind.StaticKeyword))
                .AddParameterListParameters(
                    Parameter(configParam.Identifier).WithModifiers(SyntaxTokenList.Create(Token(SyntaxKind.ThisKeyword))).WithType(IdentifierName("global::Orleans.Serialization.Configuration.TypeManifestOptions")))
                .AddBodyStatements(body.ToArray());
        }


        public override void Emit()
        {
            _metadataClassName = SyntaxGeneration.Identifier.SanitizeIdentifierName(_context.AssemblyName);
            _metadataClassNamespace = Constants.CodeGeneratorName + "." + SyntaxGeneration.Identifier.SanitizeIdentifierName(_context.AssemblyName);

            AddInvokableAndProxyClasses();
            AddSerializerTypesClasses();

            AddExtensionMethodClass();

        }

        private void AddSerializerTypesClasses()
        {
            foreach (var type in _context.SerializableTypes)
            {
                string ns = type.GeneratedNamespace;

                // Generate a partial serializer class for each serializable type.
                var serializer = Orleans.CodeGenerator.SerializerGenerator.GenerateSerializer(_context.LibraryTypes, type);

                AddClassInSeparateFile(ns, serializer);

                // Generate a copier for each serializable type.
                if (CopierGenerator.GenerateCopier(_context.LibraryTypes, type, _context.DefaultCopiers) is { } copier)
                    AddClassInSeparateFile(ns, copier);

                if (!type.IsEnumType && (!type.IsValueType && type.IsEmptyConstructable && !type.UseActivator && type is not GeneratedInvokerDescription || type.HasActivatorConstructor))
                {
                    _context.ActivatableTypes.Add(type);

                    // Generate an activator class for types with default constructor or activator constructor.
                    var activator = ActivatorGenerator.GenerateActivator(_context.LibraryTypes, type);
                    AddClassInSeparateFile(ns, activator);
                }
            }
        }

        private void AddInvokableAndProxyClasses()
        {

            foreach (var type in _context.InvokableInterfaces)
            {
                string ns = type.GeneratedNamespace;
                foreach (var method in type.Methods)
                {
                    var (invokable, generatedInvokerDescription) = InvokableGenerator.Generate(_context.LibraryTypes, type, method);
                    _context.SerializableTypes.Add(generatedInvokerDescription);
                    _context.GeneratedInvokables[method] = generatedInvokerDescription;
                    if (generatedInvokerDescription.CompoundTypeAliasArguments is { Length: > 0 } compoundTypeAliasArguments)
                    {
                        _context.CompoundTypeAliases.Add(compoundTypeAliasArguments, generatedInvokerDescription.OpenTypeSyntax);
                    }

                    AddClassInSeparateFile(ns, invokable);
                }

                var (proxy, generatedProxyDescription) = ProxyGenerator.Generate(_context.LibraryTypes, type, _metadataModel);
                _context.GeneratedProxies.Add(generatedProxyDescription);
                AddClassInSeparateFile(ns, proxy);
            }

        }

        private void AddClassInSeparateFile(string ns, ClassDeclarationSyntax cds)
        {
            var nds = GetNamespaceDeclarationSyntax(ns, _usings, cds);
            var cus = GetCompilationUnit(default, nds);

            var content = ConvertCompilationUnitSyntaxIntoString(cus);

            AddSource(cds.Identifier.Text, content);
        }

        private void AddExtensionMethodClass()
        {
            var mds = GetMethodDeclarationSyntax();
            var cds = GetClassDeclarationSyntax(mds);
            var nds = GetNamespaceDeclarationSyntax(_metadataClassNamespace, default, cds);
            var compilationUnit = GetCompilationUnit(default, nds);
            var content = ConvertCompilationUnitSyntaxIntoString(compilationUnit);
            AddSource("SerializerExtensionMethod", content);
        }

        private static List<StatementSyntax> GetStatementSyntaxes(IdentifierNameSyntax configParam)
        {
            var body = new List<StatementSyntax>();
            var addSerializerMethod = configParam.Member("Serializers").Member("Add");
            var addCopierMethod = configParam.Member("Copiers").Member("Add");
            var addConverterMethod = configParam.Member("Converters").Member("Add");
            foreach (var type in _context.SerializableTypes)
            {
                body.Add(ExpressionStatement(InvocationExpression(addSerializerMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(GetCodecTypeName(type))))))));
            }

            foreach (var type in _context.SerializableTypes)
            {
                if (type.IsEnumType) continue;

                if (!_context.DefaultCopiers.TryGetValue(type, out var typeName))
                    typeName = GetCopierTypeName(type);

                body.Add(ExpressionStatement(InvocationExpression(addCopierMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(typeName)))))));
            }

            foreach (var type in _context.DetectedCopiers)
            {
                body.Add(ExpressionStatement(InvocationExpression(addCopierMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            foreach (var type in _context.DetectedSerializers)
            {
                body.Add(ExpressionStatement(InvocationExpression(addSerializerMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            foreach (var type in _context.DetectedConverters)
            {
                body.Add(ExpressionStatement(InvocationExpression(addConverterMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            var addProxyMethod = configParam.Member("InterfaceProxies").Member("Add");
            foreach (var type in _context.GeneratedProxies)
            {
                body.Add(ExpressionStatement(InvocationExpression(addProxyMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.TypeSyntax)))))));
            }

            var addInvokableInterfaceMethod = configParam.Member("Interfaces").Member("Add");
            foreach (var type in _context.InvokableInterfaces)
            {
                body.Add(ExpressionStatement(InvocationExpression(addInvokableInterfaceMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.InterfaceType.ToOpenTypeSyntax())))))));
            }

            var addInvokableInterfaceImplementationMethod = configParam.Member("InterfaceImplementations").Member("Add");
            foreach (var type in _context.InvokableInterfaceImplementations)
            {
                body.Add(ExpressionStatement(InvocationExpression(addInvokableInterfaceImplementationMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            var addActivatorMethod = configParam.Member("Activators").Member("Add");
            foreach (var type in _context.ActivatableTypes)
            {
                body.Add(ExpressionStatement(InvocationExpression(addActivatorMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(GetActivatorTypeName(type))))))));
            }

            foreach (var type in _context.DetectedActivators)
            {
                body.Add(ExpressionStatement(InvocationExpression(addActivatorMethod,
                    ArgumentList(SingletonSeparatedList(Argument(TypeOfExpression(type.ToOpenTypeSyntax())))))));
            }

            //AddCompoundTypeAliases(configParam, body);
            return body;
        }

        public static TypeSyntax GetCodecTypeName(ISerializableTypeDescription type)
        {
            var genericArity = type.TypeParameters.Count;
            var name = Orleans.CodeGenerator.SerializerGenerator.GetSimpleClassName(type);
            if (genericArity > 0)
            {
                name = $"{name}<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }

        public static TypeSyntax GetCopierTypeName(ISerializableTypeDescription type)
        {
            var genericArity = type.TypeParameters.Count;
            var name = CopierGenerator.GetSimpleClassName(type);
            if (genericArity > 0)
            {
                name = $"{name}<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }

        public static TypeSyntax GetActivatorTypeName(ISerializableTypeDescription type)
        {
            var genericArity = type.TypeParameters.Count;
            var name = ActivatorGenerator.GetSimpleClassName(type);
            if (genericArity > 0)
            {
                name = $"{name}<{new string(',', genericArity - 1)}>";
            }

            return ParseTypeName(type.GeneratedNamespace + "." + name);
        }

        private static void AddCompoundTypeAliases(IdentifierNameSyntax configParam, List<StatementSyntax> body)
        {
            // The goal is to emit a tree describing all of the generated invokers in the form:
            // ("inv", typeof(ProxyBaseType), typeof(ContainingInterface), "<MethodId>")
            // The first step is to collate the invokers into tree to ease the process of generating a tree in code.
            var nodeId = 0;
            AddCompoundTypeAliases(body, configParam.Member("CompoundTypeAliases"), _context.CompoundTypeAliases);
            void AddCompoundTypeAliases(List<StatementSyntax> body, ExpressionSyntax tree, CompoundTypeAliasTree aliases)
            {
                ExpressionSyntax node;

                if (aliases.Key.IsDefault)
                {
                    // At the root node, do not create a new node, just enumerate over the child nodes.
                    node = tree;
                }
                else
                {
                    var nodeName = IdentifierName($"n{++nodeId}");
                    node = nodeName;
                    var valueExpression = aliases.Value switch
                    {
                        { } type => Argument(TypeOfExpression(type)),
                        _ => null
                    };

                    // Get the arguments for the Add call
                    var addArguments = aliases.Key switch
                    {
                        { IsType: true } typeKey => valueExpression switch
                        {
                            // Call the two-argument Add overload to add a key and value.
                            { } argument => new[] { Argument(TypeOfExpression(typeKey.TypeValue.ToOpenTypeSyntax())), argument },

                            // Call the one-argument Add overload to add only a key.
                            _ => new[] { Argument(TypeOfExpression(typeKey.TypeValue.ToOpenTypeSyntax())) },
                        },
                        { IsString: true } stringKey => valueExpression switch
                        {
                            // Call the two-argument Add overload to add a key and value.
                            { } argument => new[] { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(stringKey.StringValue))), argument },

                            // Call the one-argument Add overload to add only a key.
                            _ => new[] { Argument(LiteralExpression(SyntaxKind.StringLiteralExpression, Literal(stringKey.StringValue))) },
                        },
                        _ => throw new InvalidOperationException("Unexpected alias key")
                    };

                    if (aliases.Children is { Count: > 0 })
                    {
                        // C#: var {newTree.Identifier} = {tree}.Add({addArguments});
                        body.Add(LocalDeclarationStatement(VariableDeclaration(
                            ParseTypeName("var"),
                            SingletonSeparatedList(VariableDeclarator(nodeName.Identifier).WithInitializer(EqualsValueClause(InvocationExpression(
                                tree.Member("Add"),
                                ArgumentList(SeparatedList(addArguments)))))))));
                    }
                    else
                    {
                        // Do not emit a variable.
                        // C#: {tree}.Add({addArguments});
                        body.Add(ExpressionStatement(InvocationExpression(tree.Member("Add"), ArgumentList(SeparatedList(addArguments)))));
                    }
                }

                if (aliases.Children is { Count: > 0 })
                {
                    foreach (var child in aliases.Children.Values)
                    {
                        AddCompoundTypeAliases(body, node, child);
                    }
                }
            }
        }

    }
}
