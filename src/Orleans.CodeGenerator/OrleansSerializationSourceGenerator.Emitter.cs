namespace Orleans.CodeGenerator;

using System.Collections.Generic;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.SyntaxGeneration;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;
public partial class OrleansSerializationSourceGenerator
{
    private class Emitter
    {
        private SourceProductionContext _context;
        private readonly ContextGenerationSpecs _specs;

        public Emitter(SourceProductionContext context, ContextGenerationSpecs specs)
        {
            _context = context;
            _specs = specs;
        }

        public void Emit()
        {
            var compilationUnitSyntax = GenerateCode();
            var sourceString = compilationUnitSyntax.NormalizeWhitespace().ToFullString();
            AddSource($"{_specs.Compilation.AssemblyName ?? "assembly"}.orleans", sourceString);
        }


        internal CompilationUnitSyntax GenerateCode()
        {
            var nsMembers = new Dictionary<string, List<MemberDeclarationSyntax>>();

            foreach (var type in _specs.MetadataModel.InvokableInterfaces)
            {
                string ns = type.GeneratedNamespace;
                foreach (var method in type.Methods)
                {
                    var (invokable, generatedInvokerDescription) = InvokableGenerator.Generate(_specs.LibraryTypes, type, method);
                    _specs.MetadataModel.SerializableTypes.Add(generatedInvokerDescription);
                    _specs.MetadataModel.GeneratedInvokables[method] = generatedInvokerDescription;
                    if (generatedInvokerDescription.CompoundTypeAliasArguments is { Length: > 0 } compoundTypeAliasArguments)
                    {
                        _specs.MetadataModel.CompoundTypeAliases.Add(compoundTypeAliasArguments, generatedInvokerDescription.OpenTypeSyntax);
                    }

                    AddMember(ns, invokable);
                }

                var (proxy, generatedProxyDescription) = ProxyGenerator.Generate(_specs.LibraryTypes, type, _specs.MetadataModel);
                _specs.MetadataModel.GeneratedProxies.Add(generatedProxyDescription);
                AddMember(ns, proxy);
            }

            // Generate code.
            foreach (var type in _specs.MetadataModel.SerializableTypes)
            {
                string ns = type.GeneratedNamespace;

                // Generate a partial serializer class for each serializable type.
                var serializer = SerializerGenerator.GenerateSerializer(_specs.LibraryTypes, type);
                AddMember(ns, serializer);

                // Generate a copier for each serializable type.
                if (CopierGenerator.GenerateCopier(_specs.LibraryTypes, type, _specs.MetadataModel.DefaultCopiers) is { } copier)
                    AddMember(ns, copier);

                if (!type.IsEnumType && (!type.IsValueType && type.IsEmptyConstructable && !type.UseActivator && type is not GeneratedInvokerDescription || type.HasActivatorConstructor))
                {
                    _specs.MetadataModel.ActivatableTypes.Add(type);

                    // Generate an activator class for types with default constructor or activator constructor.
                    var activator = ActivatorGenerator.GenerateActivator(_specs.LibraryTypes, type);
                    AddMember(ns, activator);
                }
            }

            var metadataClassNamespace = CodeGenerator.CodeGeneratorName + "." + SyntaxGeneration.Identifier.SanitizeIdentifierName(_specs.Compilation.AssemblyName);
            var metadataClass = MetadataGenerator.GenerateMetadata(_specs.Compilation, _specs.MetadataModel, _specs.LibraryTypes);
            AddMember(ns: metadataClassNamespace, member: metadataClass);
            var metadataAttribute = AttributeList()
                .WithTarget(AttributeTargetSpecifier(Token(SyntaxKind.AssemblyKeyword)))
                .WithAttributes(
                    SingletonSeparatedList(
                        Attribute(_specs.LibraryTypes.TypeManifestProviderAttribute.ToNameSyntax())
                            .AddArgumentListArguments(AttributeArgument(TypeOfExpression(QualifiedName(IdentifierName(metadataClassNamespace), IdentifierName(metadataClass.Identifier.Text)))))));

            var assemblyAttributes = ApplicationPartAttributeGenerator.GenerateSyntax(_specs.LibraryTypes, _specs.MetadataModel);
            assemblyAttributes.Add(metadataAttribute);

            var usings = List(new[] { UsingDirective(ParseName("global::Orleans.Serialization.Codecs")), UsingDirective(ParseName("global::Orleans.Serialization.GeneratedCodeHelpers")) });
            var namespaces = new List<MemberDeclarationSyntax>(nsMembers.Count);
            foreach (var pair in nsMembers)
            {
                var ns = pair.Key;
                var member = pair.Value;

                namespaces.Add(NamespaceDeclaration(ParseName(ns)).WithMembers(List(member)).WithUsings(usings));
            }

            return CompilationUnit()
                //.WithAttributeLists(List(assemblyAttributes))
                .WithMembers(List(namespaces));

            void AddMember(string ns, MemberDeclarationSyntax member)
            {
                if (!nsMembers.TryGetValue(ns, out var existing))
                {
                    existing = nsMembers[ns] = new List<MemberDeclarationSyntax>();
                }

                existing.Add(member);
            }
        }


        private void AddSource(string fileName, string content)
        {
            _context.AddSource($"{fileName}.g.cs", content);
        }
    }
}
