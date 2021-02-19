using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGenerator.Analysis;
using Orleans.CodeGenerator.Analyzers;
using Orleans.CodeGenerator.Compatibility;
using Orleans.CodeGenerator.Generators;
using Orleans.CodeGenerator.Model;
using Orleans.CodeGenerator.Utilities;
using static Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    public class CodeGenerator
    {
        public const string ToolName = "OrleansCodeGen";
        public static readonly string Version = typeof(CodeGenerator).Assembly.GetName().Version.ToString();
        private readonly IGeneratorExecutionContext context;
        private readonly Compilation compilation;
        private readonly WellKnownTypes wellKnownTypes;
        private readonly SemanticModel semanticModelForAccessibility;
        private readonly CompilationAnalyzer compilationAnalyzer;
        private readonly SerializerTypeAnalyzer serializerTypeAnalyzer;
        private readonly SerializerGenerator serializerGenerator;
        private readonly GrainMethodInvokerGenerator grainMethodInvokerGenerator;
        private readonly GrainReferenceGenerator grainReferenceGenerator;
        private readonly CodeGeneratorOptions options;

        public CodeGenerator(IGeneratorExecutionContext context, CodeGeneratorOptions options)
        {
            this.context = context;
            this.compilation = context.Compilation;
            this.options = options;
            this.wellKnownTypes = new WellKnownTypes(compilation);
            this.compilationAnalyzer = new CompilationAnalyzer(context, this.wellKnownTypes, compilation);

            var firstSyntaxTree = compilation.SyntaxTrees.FirstOrDefault() ?? throw new InvalidOperationException("Compilation has no syntax trees.");
            this.semanticModelForAccessibility = compilation.GetSemanticModel(firstSyntaxTree);
            this.serializerTypeAnalyzer = SerializerTypeAnalyzer.Create(this.wellKnownTypes);
            this.serializerGenerator = new SerializerGenerator(this.options, this.wellKnownTypes);
            this.grainMethodInvokerGenerator = new GrainMethodInvokerGenerator(this.options, this.wellKnownTypes);
            this.grainReferenceGenerator = new GrainReferenceGenerator(this.options, this.wellKnownTypes);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        public CompilationUnitSyntax GenerateCode(CancellationToken cancellationToken)
        {
            // Create a model of the code to generate from the collection of types.
            var model = this.AnalyzeCompilation(cancellationToken);

            // Perform some validation against the generated model.
            this.ValidateModel(model, cancellationToken);

            // Finally, generate code for the model.
            return this.GenerateSyntax(model, cancellationToken);
        }

        private AggregatedModel AnalyzeCompilation(CancellationToken cancellationToken)
        {
            // Inspect the target assembly to discover known assemblies and known types.
            this.compilationAnalyzer.Analyze(cancellationToken);

            // Create a list of all distinct types from all known assemblies.
            var types = this.compilationAnalyzer
                .KnownAssemblies.SelectMany(a => a.GetDeclaredTypes())
                .Concat(this.compilationAnalyzer.KnownTypes)
                .Distinct(SymbolEqualityComparer.Default)
                .Cast<INamedTypeSymbol>()
                .ToList();

            var model = new AggregatedModel();

            // Inspect all types
            foreach (var type in types) this.compilationAnalyzer.InspectType(type);

            // Get the types which need processing.
            var (grainClasses, grainInterfaces, serializationTypes) = this.compilationAnalyzer.GetTypesToProcess();

            // Process each of the types into the model.
            foreach (var grainInterface in grainInterfaces) this.ProcessGrainInterface(model, grainInterface);
            foreach (var grainClass in grainClasses)
            {
                this.ProcessGrainClass(model, grainClass);
                this.ProcessSerializableType(model, grainClass);
            }

            foreach (var type in serializationTypes) this.ProcessSerializableType(model, type);

            foreach (var part in this.compilationAnalyzer.ApplicationParts)
            {
                model.ApplicationParts.Add(part);
            }

            this.AddAssemblyMetadata(model);

            return model;
        }

        private void AddAssemblyMetadata(AggregatedModel model)
        {
            var assembliesToScan = new List<IAssemblySymbol>();
            foreach (var asm in this.compilationAnalyzer.ReferencedAssemblies)
            {
                // Known assemblies are already handled.
                if (this.compilationAnalyzer.KnownAssemblies.Contains(asm)) continue;

                if (this.compilationAnalyzer.AssembliesExcludedFromCodeGeneration.Contains(asm)
                    || this.compilationAnalyzer.AssembliesExcludedFromMetadataGeneration.Contains(asm))
                {
                    continue;
                }

                assembliesToScan.Add(asm);
            }

            foreach (var asm in assembliesToScan)
            {
                foreach (var type in asm.GetDeclaredTypes())
                {
                    if (this.ValidForKnownTypes(type))
                    {
                        AddKnownType(model, type);
                    }
                }
            }
        }

        private void ValidateModel(AggregatedModel model, CancellationToken cancellationToken)
        {
            // Check that all types which the developer marked as requiring code generation have had code generation.
            foreach (var required in this.compilationAnalyzer.CodeGenerationRequiredTypes)
            {
                if (!model.Serializers.SerializerTypes.Any(t => SymbolEqualityComparer.Default.Equals(t.Target, required)))
                {
                    throw new CodeGenerationException(
                        $"Found {this.wellKnownTypes.ConsiderForCodeGenerationAttribute} with ThrowOnFailure set for type {required}, but a serializer" +
                        " could not be generated. Ensure that the type is accessible.");
                }
            }
        }

        private CompilationUnitSyntax GenerateSyntax(AggregatedModel model, CancellationToken cancellationToken)
        {
            var namespaceGroupings = new Dictionary<INamespaceSymbol, List<MemberDeclarationSyntax>>(SymbolEqualityComparer.Default);

            // Pass the relevant elements of the model to each of the code generators.
            foreach (var grainInterface in model.GrainInterfaces)
            {
                var nsMembers = GetNamespace(namespaceGroupings, grainInterface.Type.ContainingNamespace);
                nsMembers.Add(grainMethodInvokerGenerator.GenerateClass(grainInterface));
                nsMembers.Add(grainReferenceGenerator.GenerateClass(grainInterface));
            }

            var serializersToGenerate = model.Serializers.SerializerTypes
                .Where(s => s.SerializerTypeSyntax == null)
                .Distinct(SerializerTypeDescription.TargetComparer);
            foreach (var serializerType in serializersToGenerate)
            {
                var nsMembers = GetNamespace(namespaceGroupings, serializerType.Target.ContainingNamespace);
                TypeDeclarationSyntax generated;
                (generated, serializerType.SerializerTypeSyntax) = this.serializerGenerator.GenerateClass(this.context, this.semanticModelForAccessibility, serializerType);
                nsMembers.Add(generated);
            }

            var compilationMembers = new List<MemberDeclarationSyntax>();

            // Group the generated code by namespace since serialized types, such as the generated GrainReference classes must have a stable namespace.
            foreach (var group in namespaceGroupings)
            {
                var ns = group.Key;
                var members = group.Value;
                if (ns.IsGlobalNamespace)
                {
                    compilationMembers.AddRange(members);
                }
                else
                {
                    compilationMembers.Add(NamespaceDeclaration(ParseName(ns.ToDisplayString())).AddMembers(members.ToArray()));
                }
            }

            // Add and generate feature populators to tie everything together.
            var (attributes, featurePopulators) = FeaturePopulatorGenerator.GenerateSyntax(this.wellKnownTypes, model, this.compilation);
            compilationMembers.AddRange(featurePopulators);

            attributes.AddRange(ApplicationPartAttributeGenerator.GenerateSyntax(this.wellKnownTypes, model));

            // Add some attributes detailing which assemblies this generated code targets.
            attributes.Add(AttributeList(
                AttributeTargetSpecifier(Token(SyntaxKind.AssemblyKeyword)),
                SeparatedList(GetCodeGenerationTargetAttribute().ToArray())));

            return CompilationUnit()
                .AddUsings(UsingDirective(ParseName("global::Orleans")))
                .WithAttributeLists(List(attributes))
                .WithMembers(List(compilationMembers));

            List<MemberDeclarationSyntax> GetNamespace(Dictionary<INamespaceSymbol, List<MemberDeclarationSyntax>> namespaces, INamespaceSymbol ns)
            {
                if (namespaces.TryGetValue(ns, out var result)) return result;
                return namespaces[ns] = new List<MemberDeclarationSyntax>();
            }

            IEnumerable<AttributeSyntax> GetCodeGenerationTargetAttribute()
            {
                yield return GenerateAttribute(this.compilation.Assembly);

                foreach (var assembly in this.compilationAnalyzer.ReferencedAssemblies)
                {
                    if (this.compilationAnalyzer.AssembliesExcludedFromCodeGeneration.Contains(assembly) ||
                        this.compilationAnalyzer.AssembliesExcludedFromMetadataGeneration.Contains(assembly))
                    {
                        continue;
                    }

                    yield return GenerateAttribute(assembly);
                }

                AttributeSyntax GenerateAttribute(IAssemblySymbol assembly)
                {
                    var assemblyName = assembly.Identity.GetDisplayName(fullKey: true);
                    var nameSyntax = this.wellKnownTypes.OrleansCodeGenerationTargetAttribute.ToNameSyntax();
                    return Attribute(nameSyntax)
                        .AddArgumentListArguments(AttributeArgument(assemblyName.ToLiteralExpression()));
                }
            }
        }

        private void ProcessGrainInterface(AggregatedModel model, INamedTypeSymbol type)
        {
            var accessible = this.semanticModelForAccessibility.IsAccessible(0, type);

            if (accessible)
            {
                var genericMethod = type.GetInstanceMembers<IMethodSymbol>().FirstOrDefault(m => m.IsGenericMethod);
                if (genericMethod != null && this.wellKnownTypes.GenericMethodInvoker is WellKnownTypes.None)
                {
                    var declaration = genericMethod.GetDeclarationSyntax();
                    this.context.ReportDiagnostic(GenericMethodRequireOrleansCoreDiagnostic.CreateDiagnostic(declaration));
                }

                var methods = GetGrainMethodDescriptions(type);

                model.GrainInterfaces.Add(new GrainInterfaceDescription(
                    type,
                    this.wellKnownTypes.GetTypeId(type),
                    this.wellKnownTypes.GetVersion(type),
                    methods));
            }

            // Returns a list of all methods in all interfaces on the provided type.
            IEnumerable<GrainMethodDescription> GetGrainMethodDescriptions(INamedTypeSymbol initialType)
            {
                IEnumerable<INamedTypeSymbol> GetAllInterfaces(INamedTypeSymbol s)
                {
                    if (s.TypeKind == TypeKind.Interface)
                        yield return s;
                    foreach (var i in s.AllInterfaces) yield return i;
                }

                foreach (var iface in GetAllInterfaces(initialType))
                {
                    foreach (var method in iface.GetDeclaredInstanceMembers<IMethodSymbol>())
                    {
                        yield return new GrainMethodDescription(this.wellKnownTypes.GetMethodId(method), method);
                    }
                }
            }
        }

        private void ProcessGrainClass(AggregatedModel model, INamedTypeSymbol type)
        {
            var accessible = this.semanticModelForAccessibility.IsAccessible(0, type);

            if (accessible)
            {
                model.GrainClasses.Add(new GrainClassDescription(type, this.wellKnownTypes.GetTypeId(type)));
            }
            else
            {
                var declaration = type.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax() as TypeDeclarationSyntax;
                this.context.ReportDiagnostic(InaccessibleGrainClassDiagnostic.CreateDiagnostic(declaration));
            }
        }

        private void ProcessSerializableType(AggregatedModel model, INamedTypeSymbol type)
        {
            if (!ValidForKnownTypes(type))
            {
                return;
            }

            AddKnownType(model, type);

            var serializerModel = model.Serializers;

            if (type.IsAbstract)
            {
                return;
            }

            // Ensure that the type is accessible from generated code.
            var accessible = this.semanticModelForAccessibility.IsAccessible(0, type);
            if (!accessible)
            {
                return;
            }

            if (type.HasBaseType(this.wellKnownTypes.Exception))
            {
                return;
            }

            if (type.HasBaseType(this.wellKnownTypes.Delegate))
            {
                return;
            }

            if (type.AllInterfaces.Contains(this.wellKnownTypes.IAddressable))
            {
                return;
            }

            // Account for types that serialize themselves and/or are serializers for other types.
            var selfSerializing = false;
            if (this.serializerTypeAnalyzer.IsSerializer(type, out var serializerTargets))
            {
                var typeSyntax = type.ToTypeSyntax();
                foreach (var target in serializerTargets)
                {
                    if (SymbolEqualityComparer.Default.Equals(target, type))
                    {
                        selfSerializing = true;
                        typeSyntax = type.WithoutTypeParameters().ToTypeSyntax();
                    }

                    serializerModel.SerializerTypes.Add(new SerializerTypeDescription
                    {
                        Target = target,
                        SerializerTypeSyntax = typeSyntax,
                        OverrideExistingSerializer = true
                    });
                }
            }

            if (selfSerializing)
            {
                return;
            }

            if (type.HasAttribute(this.wellKnownTypes.GeneratedCodeAttribute))
            {
                return;
            }

            if (type.IsStatic)
            {
                return;
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                return;
            }

            if (type.TypeParameters.Any(p => p.ConstraintTypes.Any(c => SymbolEqualityComparer.Default.Equals(c, this.wellKnownTypes.Delegate))))
            {
                return;
            }

            var isSerializable = this.compilationAnalyzer.IsSerializable(type);
            if (isSerializable && this.compilationAnalyzer.IsFromKnownAssembly(type))
            {
                // Skip types which have fields whose types are inaccessible from generated code.
                foreach (var field in type.GetInstanceMembers<IFieldSymbol>())
                {
                    // Ignore fields which won't be serialized anyway.
                    if (!this.serializerGenerator.ShouldSerializeField(field))
                    {
                                                continue;
                    }

                    // Check field type accessibility.
                    var fieldAccessible = this.semanticModelForAccessibility.IsAccessible(0, field.Type);
                    if (!fieldAccessible)
                    {
                        return;
                    }
                }

                // Add the type that needs generation.
                // The serializer generator will fill in the missing SerializerTypeSyntax field with the
                // generated type.
                serializerModel.SerializerTypes.Add(new SerializerTypeDescription
                {
                    Target = type
                });
            }
        }

        private static void AddKnownType(AggregatedModel model, INamedTypeSymbol type)
        {
            // Many types which will never have a serializer generated are still added to known types so that they can be used to identify the type
            // in a serialized payload. For example, when serializing List<SomeAbstractType>, SomeAbstractType must be known. The same applies to
            // interfaces (which are encoded as abstract).
            var serializerModel = model.Serializers;
            var strippedType = type.WithoutTypeParameters();
            serializerModel.KnownTypes.Add(new KnownTypeDescription(strippedType));
        }

        private bool ValidForKnownTypes(INamedTypeSymbol type)
        {
            // Skip implicitly declared types like anonymous classes and closures.
            if (type.IsImplicitlyDeclared)
            {
                return false;
            }

            if (!type.CanBeReferencedByName)
            {
                return false;
            }

            if (type.SpecialType != SpecialType.None)
            {
                return false;
            }

            switch (type.TypeKind)
            {
                case TypeKind.Unknown:
                case TypeKind.Array:
                case TypeKind.Delegate:
                case TypeKind.Dynamic:
                case TypeKind.Error:
                case TypeKind.Module:
                case TypeKind.Pointer:
                case TypeKind.TypeParameter:
                case TypeKind.Submission:
                    return false;
            }

            if (type.IsStatic)
            {
                return false;
            }

            if (type.HasUnsupportedMetadata)
            {
                return false;
            }

            return true;
        }
    }
}