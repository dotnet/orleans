namespace Orleans.CodeGenerator
{
    using System;
    using System.CodeDom.Compiler;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;

    using Microsoft.CodeAnalysis.CSharp;
    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using Orleans.Async;
    using Orleans.CodeGeneration;
    using Orleans.CodeGenerator.Utilities;
    using Orleans.Runtime;

    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    public class CodeGenerator : IRuntimeCodeGenerator, ISourceCodeGenerator
    {
        /// <summary>
        /// The types which require a serializer.
        /// </summary>
        private static readonly SerializableTypeCollector SerializerRequired = new SerializableTypeCollector();

        /// <summary>
        /// The types which require source code for a serializer.
        /// </summary>
        private static readonly SerializableTypeCollector SerializerSourceRequired =
            new SerializableTypeCollector(includeNonPublic: true);

        /// <summary>
        /// The compiled assemblies.
        /// </summary>
        private static readonly ConcurrentDictionary<Assembly, bool> CompiledAssemblies =
            new ConcurrentDictionary<Assembly, bool>();

        /// <summary>
        /// The logger.
        /// </summary>
        private static readonly Logger Logger = TraceLogger.GetLogger("CodeGenerator");

        /// <summary>
        /// The static instance.
        /// </summary>
        private static readonly CodeGenerator StaticInstance = new CodeGenerator();

        /// <summary>
        /// Gets or sets the static instance.
        /// </summary>
        public static CodeGenerator Instance
        {
            get
            {
                return StaticInstance;
            }
        }

        public void GenerateAndLoadForAllAssemblies()
        {
            this.GenerateAndLoadForAssemblies(AppDomain.CurrentDomain.GetAssemblies());
        }

        public void GenerateAndLoadForAssemblies(params Assembly[] inputs)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException("inputs");
            }

            var timer = Stopwatch.StartNew();
            var grainAssemblies =
                inputs.Where(
                    _ =>
                    !_.IsDynamic && !CompiledAssemblies.ContainsKey(_)
                    && _.GetCustomAttribute<GeneratedCodeAttribute>() == null).ToList();
            if (grainAssemblies.Count == 0)
            {
                // Already up to date.
                return;
            }

            // Generate code for newly loaded assemblies.
            var generated = GenerateForAssemblies(grainAssemblies, true);

            if (generated.Syntax != null)
            {
                CompileAndLoad(generated);
            }

            foreach (var assembly in generated.SourceAssemblies)
            {
                CompiledAssemblies.TryAdd(assembly, true);
            }

            Logger.Info(
                (int)ErrorCode.CodeGenCompilationSucceeded,
                "Generated code for " + generated.SourceAssemblies.Count + " assemblies in " + timer.ElapsedMilliseconds
                + "ms");
        }

        public void GenerateAndLoadForAssembly(Assembly input)
        {
            var timer = Stopwatch.StartNew();
            if (CompiledAssemblies.ContainsKey(input) || input.GetCustomAttribute<GeneratedCodeAttribute>() != null)
            {
                return;
            }

            var generated = GenerateForAssemblies(new List<Assembly> { input }, true);

            if (generated.Syntax != null)
            {
                CompileAndLoad(generated);
            }

            foreach (var assembly in generated.SourceAssemblies)
            {
                CompiledAssemblies.TryAdd(assembly, true);
            }

            Logger.Info(
                (int)ErrorCode.CodeGenCompilationSucceeded,
                "Generated code for 1 assembly in " + timer.ElapsedMilliseconds + "ms");
        }

        public string GenerateSourceForAssembly(Assembly input)
        {
            var generated = GenerateForAssemblies(new List<Assembly> { input }, false);
            return CodeGeneratorCommon.GenerateSourceCode(generated.Syntax);
        }

        private static void CompileAndLoad(GeneratedSyntax generatedSyntax)
        {
            var code =
                generatedSyntax.Syntax.AddAttributeLists(
                    SF.AttributeList()
                        .AddAttributes(
                            SF.Attribute(typeof(GeneratedCodeAttribute).GetNameSyntax())
                                .AddArgumentListArguments(
                                    SF.AttributeArgument("Orleans-CodeGenerator".GetLiteralExpression()),
                                    SF.AttributeArgument(RuntimeVersion.FileVersion.GetLiteralExpression())))
                        .WithTarget(SF.AttributeTargetSpecifier(SF.Token(SyntaxKind.AssemblyKeyword))));
            var generatedAssembly = CodeGeneratorCommon.CompileAssembly(code, "OrleansCodeGen.dll");

            GrainReferenceGenerator.RegisterGrainReferenceSerializers(generatedAssembly);
        }

        private static GeneratedSyntax GenerateForAssemblies(List<Assembly> assemblies, bool runtime)
        {
            SerializableTypeCollector serializationCollector;
            HashSet<Type> ignoreTypes;
            if (runtime)
            {
                // Ignore types which have already been accounted for.
                ignoreTypes = CodeGeneratorCommon.GetTypesWithImplementations(
                    typeof(MethodInvokerAttribute),
                    typeof(GrainReferenceAttribute),
                    typeof(GrainStateAttribute),
                    typeof(SerializerAttribute));
                serializationCollector = SerializerRequired;
            }
            else
            {
                ignoreTypes = new HashSet<Type>();
                serializationCollector = SerializerSourceRequired;
            }

            var usings =
                    TypeUtils.GetNamespaces(typeof(TaskUtility), typeof(GrainExtensions))
                        .Select(_ => SyntaxFactory.UsingDirective(SyntaxFactory.ParseName(_)))
                        .ToArray();

            var members = new List<MemberDeclarationSyntax>();
            var allTypes =
                assemblies.Where(_ => !_.IsDynamic && _.GetCustomAttribute<GeneratedCodeAttribute>() == null)
                    .SelectMany(_ => _.GetTypes())
                    .ToList();

            // Collect the types which require code generation.
            var grainTypes = new HashSet<Type>();
            foreach (var type in allTypes)
            {
                if (SerializerGenerator.IsSerializationSeedType(type))
                {
                    serializationCollector.Consider(type);
                }
                
                if (serializationCollector.HasMore())
                {
                    grainTypes.UnionWith(serializationCollector.TakeAll());
                }

                if (CodeGeneratorCommon.IsGrainInterfaceType(type) || GrainStateGenerator.IsStatefulGrain(type))
                {
                    grainTypes.Add(type);
                }
            }

            grainTypes.UnionWith(serializationCollector.TakeAll());
            grainTypes.RemoveWhere(_ => ignoreTypes.Contains(_) || (runtime && !_.IsPublic));

            // Group the types by namespace and generate the required code.
            foreach (var group in grainTypes.GroupBy(_ => CodeGeneratorCommon.GetGeneratedNamespace(_)))
            {
                var namespaceMembers = new List<MemberDeclarationSyntax>();
                foreach (var type in group)
                {
                    if (CodeGeneratorCommon.IsGrainInterfaceType(type))
                    {
                        namespaceMembers.Add(GrainMethodInvokerGenerator.GenerateClass(type));
                        namespaceMembers.Add(GrainReferenceGenerator.GenerateClass(type));
                    }

                    if (GrainStateGenerator.IsStatefulGrain(type))
                    {
                        namespaceMembers.Add(GrainStateGenerator.GenerateClass(type));
                    }

                    if (serializationCollector.IsSerializationRequired(type))
                    {
                        namespaceMembers.AddRange(SerializerGenerator.GenerateClass(type));
                    }
                }

                if (namespaceMembers.Count == 0)
                {
                    continue;
                }

                members.Add(
                    SyntaxFactory.NamespaceDeclaration(SyntaxFactory.ParseName(group.Key))
                        .AddUsings(usings)
                        .AddMembers(namespaceMembers.ToArray()));
            }

            return new GeneratedSyntax
            {
                SourceAssemblies = assemblies,
                Syntax = members.Count > 0 ? SF.CompilationUnit().AddMembers(members.ToArray()) : null
            };
        }

        private class GeneratedSyntax
        {
            public List<Assembly> SourceAssemblies { get; set; }
            public CompilationUnitSyntax Syntax { get; set; }
        }
    }
}