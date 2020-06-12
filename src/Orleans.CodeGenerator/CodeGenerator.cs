using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.Extensions.Logging;
using Orleans.CodeGenerator.Analysis;
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

        private readonly Compilation compilation;
        private readonly ILogger log;
        private readonly WellKnownTypes wellKnownTypes;
        private readonly SemanticModel semanticModelForAccessibility;
        private readonly CompilationAnalyzer compilationAnalyzer;
        private readonly SerializerTypeAnalyzer serializerTypeAnalyzer;
        private readonly SerializerGenerator serializerGenerator;
        private readonly GrainMethodInvokerGenerator grainMethodInvokerGenerator;
        private readonly GrainReferenceGenerator grainReferenceGenerator;
        private readonly CodeGeneratorOptions options;

        public CodeGenerator(Compilation compilation, CodeGeneratorOptions options, ILogger log)
        {
            this.compilation = compilation;
            this.options = options;
            this.log = log;
            this.wellKnownTypes = new WellKnownTypes(compilation);
            this.compilationAnalyzer = new CompilationAnalyzer(log, this.wellKnownTypes, compilation);

            var firstSyntaxTree = compilation.SyntaxTrees.FirstOrDefault() ?? throw new InvalidOperationException("Compilation has no syntax trees.");
            this.semanticModelForAccessibility = compilation.GetSemanticModel(firstSyntaxTree);
            this.serializerTypeAnalyzer = SerializerTypeAnalyzer.Create(this.wellKnownTypes);
            this.serializerGenerator = new SerializerGenerator(this.options, this.wellKnownTypes);
            this.grainMethodInvokerGenerator = new GrainMethodInvokerGenerator(this.options, this.wellKnownTypes);
            this.grainReferenceGenerator = new GrainReferenceGenerator(this.options, this.wellKnownTypes);
        }

        public CompilationUnitSyntax GenerateCode(CancellationToken cancellationToken)
        {
            // Create a model of the code to generate from the collection of types.
            var model = this.AnalyzeCompilation();

            // Perform some validation against the generated model.
            this.ValidateModel(model);

            // Finally, generate code for the model.
            return this.GenerateSyntax(model);
        }

        private AggregatedModel AnalyzeCompilation()
        {
            // Inspect the target assembly to discover known assemblies and known types.
            if (log.IsEnabled(LogLevel.Debug)) log.LogDebug($"Main assembly {this.compilation.Assembly}");
            this.compilationAnalyzer.Analyze();

            // Create a list of all distinct types from all known assemblies.
            var types = this.compilationAnalyzer
                .KnownAssemblies.SelectMany(a => a.GetDeclaredTypes())
                .Concat(this.compilationAnalyzer.KnownTypes)
                .Distinct()
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
                    this.log.LogDebug($"Skipping adding known types for assembly {asm.Identity.Name} since a referenced assembly already includes its types.");
                    continue;
                }

                assembliesToScan.Add(asm);
            }

            foreach (var asm in assembliesToScan)
            {
                this.log.LogDebug($"Generating metadata for referenced assembly {asm.Identity.Name}.");
                foreach (var type in asm.GetDeclaredTypes())
                {
                    if (this.ValidForKnownTypes(type))
                    {
                        AddKnownType(model, type);
                    }
                }
            }
        }

        private void ValidateModel(AggregatedModel model)
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

        private CompilationUnitSyntax GenerateSyntax(AggregatedModel model)
        {
            var namespaceGroupings = new Dictionary<INamespaceSymbol, List<MemberDeclarationSyntax>>();

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
                (generated, serializerType.SerializerTypeSyntax) = this.serializerGenerator.GenerateClass(this.semanticModelForAccessibility, serializerType, this.log);
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
                    this.log.LogTrace($"Adding [assembly: OrleansCodeGenerationTarget(\"{assemblyName}\")]");
                    var nameSyntax = this.wellKnownTypes.OrleansCodeGenerationTargetAttribute.ToNameSyntax();
                    return Attribute(nameSyntax)
                        .AddArgumentListArguments(AttributeArgument(assemblyName.ToLiteralExpression()));
                }
            }
        }

        private void ProcessGrainInterface(AggregatedModel model, INamedTypeSymbol type)
        {
            var accessible = this.semanticModelForAccessibility.IsAccessible(0, type);

            if (this.log.IsEnabled(LogLevel.Debug))
            {
                this.log.LogDebug($"Found grain interface {type.ToDisplayString()}{(accessible ? string.Empty : ", but it is inaccessible")}");
            }

            if (accessible)
            {
                var genericMethod = type.GetInstanceMembers<IMethodSymbol>().FirstOrDefault(m => m.IsGenericMethod);
                if (genericMethod != null && this.wellKnownTypes.GenericMethodInvoker is WellKnownTypes.None)
                {
                    if (this.log.IsEnabled(LogLevel.Warning))
                    {
                        var message = $"Grain interface {type} has a generic method, {genericMethod}." +
                                      " Support for generic methods requires the project to reference Microsoft.Orleans.Core, but this project does not reference it.";
                        this.log.LogError(message);
                        throw new CodeGenerationException(message);
                    }
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

            if (this.log.IsEnabled(LogLevel.Debug))
            {
                this.log.LogDebug($"Found grain class {type.ToDisplayString()}{(accessible ? string.Empty : ", but it is inaccessible")}");
            }

            if (accessible)
            {
                model.GrainClasses.Add(new GrainClassDescription(type, this.wellKnownTypes.GetTypeId(type)));
            }
        }

        private void ProcessSerializableType(AggregatedModel model, INamedTypeSymbol type)
        {
            if (!ValidForKnownTypes(type))
            {
                if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace($"{nameof(ProcessSerializableType)} skipping abstract type {type}");
                return;
            }

            AddKnownType(model, type);

            var serializerModel = model.Serializers;

            if (type.IsAbstract)
            {
                if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace($"{nameof(ProcessSerializableType)} skipping abstract type {type}");
                return;
            }

            // Ensure that the type is accessible from generated code.
            var accessible = this.semanticModelForAccessibility.IsAccessible(0, type);
            if (!accessible)
            {
                if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace($"{nameof(ProcessSerializableType)} skipping inaccessible type {type}");
                return;
            }

            if (type.HasBaseType(this.wellKnownTypes.Exception))
            {
                if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace($"{nameof(ProcessSerializableType)} skipping Exception type {type}");
                return;
            }

            if (type.HasBaseType(this.wellKnownTypes.Delegate))
            {
                if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace($"{nameof(ProcessSerializableType)} skipping Delegate type {type}");
                return;
            }

            if (type.AllInterfaces.Contains(this.wellKnownTypes.IAddressable))
            {
                if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace($"{nameof(ProcessSerializableType)} skipping IAddressable type {type}");
                return;
            }

            // Account for types that serialize themselves and/or are serializers for other types.
            var selfSerializing = false;
            if (this.serializerTypeAnalyzer.IsSerializer(type, out var serializerTargets))
            {
                var typeSyntax = type.ToTypeSyntax();
                foreach (var target in serializerTargets)
                {
                    if (this.log.IsEnabled(LogLevel.Trace))
                    {
                        this.log.LogTrace($"{nameof(ProcessSerializableType)} type {type} is a serializer for {target}");
                    }

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
                if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace($"{nameof(ProcessSerializableType)} skipping serializer generation for self-serializing type {type}");
                return;
            }

            if (type.HasAttribute(this.wellKnownTypes.GeneratedCodeAttribute))
            {
                if (this.log.IsEnabled(LogLevel.Trace))
                {
                    this.log.LogTrace($"{nameof(ProcessSerializableType)} type {type} is a generated type and no serializer will be generated for it");
                }

                return;
            }

            if (type.IsStatic)
            {
                if (this.log.IsEnabled(LogLevel.Trace))
                {
                    this.log.LogTrace($"{nameof(ProcessSerializableType)} type {type} is a static type and no serializer will be generated for it");
                }

                return;
            }

            if (type.TypeKind == TypeKind.Enum)
            {
                if (this.log.IsEnabled(LogLevel.Trace))
                {
                    this.log.LogTrace($"{nameof(ProcessSerializableType)} type {type} is an enum type and no serializer will be generated for it");
                }

                return;
            }

            if (type.TypeParameters.Any(p => p.ConstraintTypes.Any(c => SymbolEqualityComparer.Default.Equals(c, this.wellKnownTypes.Delegate))))
            {
                if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace($"{nameof(ProcessSerializableType)} skipping type with Delegate parameter constraint, {type}");
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
                        if (this.log.IsEnabled(LogLevel.Trace))
                        {
                            this.log.LogTrace($"{nameof(ProcessSerializableType)} skipping non-serialized field {field} in type {type}");
                        }

                        continue;
                    }

                    // Check field type accessibility.
                    var fieldAccessible = this.semanticModelForAccessibility.IsAccessible(0, field.Type);
                    if (!fieldAccessible)
                    {
                        if (this.log.IsEnabled(LogLevel.Trace))
                        {
                            this.log.LogTrace($"{nameof(ProcessSerializableType)} skipping type {type} with inaccessible field type {field.Type} (field: {field})");
                        }

                        return;
                    }
                }

                // Add the type that needs generation.
                // The serializer generator will fill in the missing SerializerTypeSyntax field with the
                // generated type.
                if (this.log.IsEnabled(LogLevel.Trace))
                {
                    this.log.LogTrace($"{nameof(ProcessSerializableType)} will generate a serializer for type {type}");
                }

                serializerModel.SerializerTypes.Add(new SerializerTypeDescription
                {
                    Target = type
                });
            }
            else if (this.log.IsEnabled(LogLevel.Trace))
            {
                this.log.LogTrace($"{nameof(ProcessSerializableType)} will not generate a serializer for type {type}");
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

            if (this.log.IsEnabled(LogLevel.Trace)) this.log.LogTrace($"{nameof(ValidForKnownTypes)} adding type {type}");

            return true;
        }
    }
}