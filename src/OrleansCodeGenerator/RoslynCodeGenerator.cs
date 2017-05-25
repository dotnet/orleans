using Orleans.Serialization;

namespace Orleans.CodeGenerator
{
    using System;
    using System.CodeDom.Compiler;
    using System.Collections.Concurrent;
    using System.Collections.Generic;
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;

    using Microsoft.CodeAnalysis.CSharp.Syntax;
    using Orleans.Async;
    using Orleans.CodeGeneration;
    using Orleans.Runtime;
    using GrainInterfaceUtils = Orleans.CodeGeneration.GrainInterfaceUtils;
    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    /// <summary>
    /// Implements a code generator using the Roslyn C# compiler.
    /// </summary>
    public class RoslynCodeGenerator : IRuntimeCodeGenerator, ISourceCodeGenerator, ICodeGeneratorCache
    {
        /// <summary>
        /// The compiled assemblies.
        /// </summary>
        private static readonly ConcurrentDictionary<string, CachedAssembly> CompiledAssemblies =
            new ConcurrentDictionary<string, CachedAssembly>();

        /// <summary>
        /// The logger.
        /// </summary>
        private static readonly Logger Logger = LogManager.GetLogger("CodeGenerator");

        /// <summary>
        /// The serializer generation manager.
        /// </summary>
        private static readonly SerializerGenerationManager SerializerGenerationManager = new SerializerGenerationManager();
        
        /// <summary>
        /// Adds a pre-generated assembly.
        /// </summary>
        /// <param name="targetAssemblyName">
        /// The name of the assembly the provided <paramref name="generatedAssembly"/> targets.
        /// </param>
        /// <param name="generatedAssembly">
        /// The generated assembly.
        /// </param>
        public void AddGeneratedAssembly(string targetAssemblyName, GeneratedAssembly generatedAssembly)
        {
            CompiledAssemblies.TryAdd(targetAssemblyName, new CachedAssembly(generatedAssembly));
        }

        /// <summary>
        /// Generates code for all loaded assemblies and loads the output.
        /// </summary>
        public IReadOnlyList<GeneratedAssembly> GenerateAndLoadForAllAssemblies()
        {
            return this.GenerateAndLoadForAssemblies(AppDomain.CurrentDomain.GetAssemblies());
        }

        /// <summary>
        /// Generates and loads code for the specified inputs.
        /// </summary>
        /// <param name="inputs">The assemblies to generate code for.</param>
        public IReadOnlyList<GeneratedAssembly> GenerateAndLoadForAssemblies(params Assembly[] inputs)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException(nameof(inputs));
            }

            var results = new List<GeneratedAssembly>();

            var timer = Stopwatch.StartNew();
            var emitDebugSymbols = false;
            foreach (var input in inputs)
            {
                if (!emitDebugSymbols)
                {
                    emitDebugSymbols |= RuntimeVersion.IsAssemblyDebugBuild(input);
                }

                RegisterGeneratedCodeTargets(input);
                var cached = TryLoadGeneratedAssemblyFromCache(input);
                if (cached != null)
                {
                    results.Add(cached);
                }
            }

            var grainAssemblies = inputs.Where(ShouldGenerateCodeForAssembly).ToList();
            if (grainAssemblies.Count == 0)
            {
                // Already up to date.
                return results;
            }

            try
            {
                // Generate code for newly loaded assemblies.
                var generatedSyntax = GenerateForAssemblies(grainAssemblies, true);

                CachedAssembly generatedAssembly;
                if (generatedSyntax.Syntax != null)
                {
                    generatedAssembly = CompileAndLoad(generatedSyntax, emitDebugSymbols);
                    if (generatedAssembly != null)
                    {
                        results.Add(generatedAssembly);
                    }
                }
                else
                {
                    generatedAssembly = new CachedAssembly { Loaded = true };
                }

                foreach (var assembly in generatedSyntax.SourceAssemblies)
                {
                    CompiledAssemblies.AddOrUpdate(
                        assembly.GetName().FullName,
                        generatedAssembly,
                        (_, __) => generatedAssembly);
                }

                if (Logger.IsVerbose2)
                {
                    Logger.Verbose2(
                        ErrorCode.CodeGenCompilationSucceeded,
                        "Generated code for {0} assemblies in {1}ms",
                        generatedSyntax.SourceAssemblies.Count,
                        timer.ElapsedMilliseconds);
                }

                return results;
            }
            catch (Exception exception)
            {
                var assemblyNames = string.Join("\n", grainAssemblies.Select(_ => _.GetName().FullName));
                var message =
                    $"Exception generating code for input assemblies:\n{assemblyNames}\nException: {LogFormatter.PrintException(exception)}";
                Logger.Warn(ErrorCode.CodeGenCompilationFailed, message, exception);
                throw;
            }
        }

        /// <summary>
        /// Ensures that code generation has been run for the provided assembly.
        /// </summary>
        /// <param name="input">
        /// The assembly to generate code for.
        /// </param>
        public GeneratedAssembly GenerateAndLoadForAssembly(Assembly input)
        {
            try
            {
                RegisterGeneratedCodeTargets(input);
                if (!ShouldGenerateCodeForAssembly(input))
                {
                    return TryLoadGeneratedAssemblyFromCache(input);
                }

                var timer = Stopwatch.StartNew();
                var generated = GenerateForAssemblies(new List<Assembly> { input }, true);

                CachedAssembly generatedAssembly;
                if (generated.Syntax != null)
                {
                    var emitDebugSymbols = RuntimeVersion.IsAssemblyDebugBuild(input);
                    generatedAssembly = CompileAndLoad(generated, emitDebugSymbols);
                }
                else
                {
                    generatedAssembly = new CachedAssembly { Loaded = true };
                }

                foreach (var assembly in generated.SourceAssemblies)
                {
                    CompiledAssemblies.AddOrUpdate(
                        assembly.GetName().FullName,
                        generatedAssembly,
                        (_, __) => generatedAssembly);
                }

                if (Logger.IsVerbose2)
                {
                    Logger.Verbose2(
                        ErrorCode.CodeGenCompilationSucceeded,
                        "Generated code for 1 assembly in {0}ms",
                        timer.ElapsedMilliseconds);
                }

                return generatedAssembly;
            }
            catch (Exception exception)
            {
                var message =
                    $"Exception generating code for input assembly {input.GetName().FullName}\nException: {LogFormatter.PrintException(exception)}";
                Logger.Warn(ErrorCode.CodeGenCompilationFailed, message, exception);
                throw;
            }
        }

        /// <summary>
        /// Generates source code for the provided assembly.
        /// </summary>
        /// <param name="input">
        /// The assembly to generate source for.
        /// </param>
        /// <returns>
        /// The generated source.
        /// </returns>
        public string GenerateSourceForAssembly(Assembly input)
        {
            RegisterGeneratedCodeTargets(input);
            if (!ShouldGenerateCodeForAssembly(input))
            {
                return string.Empty;
            }

            var generated = GenerateForAssemblies(new List<Assembly> { input }, false);
            if (generated.Syntax == null)
            {
                return string.Empty;
            }

            return CodeGeneratorCommon.GenerateSourceCode(CodeGeneratorCommon.AddGeneratedCodeAttribute(generated));
        }

        /// <summary>
        /// Returns the collection of generated assemblies as pairs of target assembly name to raw assembly bytes.
        /// </summary>
        /// <returns>The collection of generated assemblies.</returns>
        public IDictionary<string, GeneratedAssembly> GetGeneratedAssemblies()
        {
            return CompiledAssemblies.ToDictionary(_ => _.Key, _ => (GeneratedAssembly)_.Value);
        }

        /// <summary>
        /// Attempts to load a generated assembly from the cache.
        /// </summary>
        /// <param name="targetAssembly">
        /// The target assembly which the cached counterpart is generated for.
        /// </param>
        private static CachedAssembly TryLoadGeneratedAssemblyFromCache(Assembly targetAssembly)
        {
            CachedAssembly cached;
            if (!CompiledAssemblies.TryGetValue(targetAssembly.GetName().FullName, out cached)
                || cached.RawBytes == null || cached.Loaded)
            {
                return cached;
            }

            // Load the assembly and mark it as being loaded.
            cached.Assembly = LoadAssembly(cached);
            cached.Loaded = true;
            return cached;
        }

        /// <summary>
        /// Compiles the provided syntax tree, and loads and returns the result.
        /// </summary>
        /// <param name="generatedSyntax">The syntax tree.</param>
        /// <param name="emitDebugSymbols">
        /// Whether or not to emit debug symbols for the generated assembly.
        /// </param>
        /// <returns>The compilation output.</returns>
        private static CachedAssembly CompileAndLoad(GeneratedSyntax generatedSyntax, bool emitDebugSymbols)
        {
            var generated = CodeGeneratorCommon.CompileAssembly(generatedSyntax, "OrleansCodeGen", emitDebugSymbols: emitDebugSymbols);
            var loadedAssembly = LoadAssembly(generated);
            return new CachedAssembly(generated)
            {
                Loaded = true,
                Assembly = loadedAssembly,
            };
        }

        /// <summary>
        /// Loads the specified assembly.
        /// </summary>
        /// <param name="asm">The assembly to load.</param>
        private static Assembly LoadAssembly(GeneratedAssembly asm)
        {
#if ORLEANS_BOOTSTRAP
            throw new NotImplementedException();
#elif NETSTANDARD
            Assembly result;
            result = Orleans.PlatformServices.PlatformAssemblyLoader.LoadFromBytes(asm.RawBytes, asm.DebugSymbolRawBytes);
            AppDomain.CurrentDomain.AddAssembly(result);
            return result;
#else
            if (asm.DebugSymbolRawBytes != null)
            {
                return Assembly.Load(
                    asm.RawBytes,
                    asm.DebugSymbolRawBytes);
            }
            else
            {
                return Assembly.Load(asm.RawBytes);
            }
#endif
        }

        /// <summary>
        /// Generates a syntax tree for the provided assemblies.
        /// </summary>
        /// <param name="assemblies">The assemblies to generate code for.</param>
        /// <param name="runtime">Whether or not runtime code generation is being performed.</param>
        /// <returns>The generated syntax tree.</returns>
        private static GeneratedSyntax GenerateForAssemblies(List<Assembly> assemblies, bool runtime)
        {
            if (Logger.IsVerbose)
            {
                Logger.Verbose(
                    "Generating code for assemblies: {0}",
                    string.Join(", ", assemblies.Select(_ => _.FullName)));
            }

            Assembly targetAssembly;
            HashSet<Type> ignoredTypes;
            if (runtime)
            {
                // Ignore types which have already been accounted for.
                ignoredTypes = GetTypesWithGeneratedSupportClasses();
                targetAssembly = null;
            }
            else
            {
                ignoredTypes = new HashSet<Type>();
                targetAssembly = assemblies.FirstOrDefault();
            }

            var members = new List<MemberDeclarationSyntax>();

            // Include assemblies which are marked as included.
            var knownAssemblyAttributes = new Dictionary<Assembly, KnownAssemblyAttribute>();
            var knownAssemblies = new HashSet<Assembly>();
            foreach (var attribute in assemblies.SelectMany(asm => asm.GetCustomAttributes<KnownAssemblyAttribute>()))
            {
                knownAssemblyAttributes[attribute.Assembly] = attribute;
                knownAssemblies.Add(attribute.Assembly);
            }

            if (knownAssemblies.Count > 0)
            {
                knownAssemblies.UnionWith(assemblies);
                assemblies = knownAssemblies.ToList();
            }

            // Get types from assemblies which reference Orleans and are not generated assemblies.
            var includedTypes = new HashSet<Type>();
            for (var i = 0; i < assemblies.Count; i++)
            {
                var assembly = assemblies[i];
                foreach (var attribute in assembly.GetCustomAttributes<ConsiderForCodeGenerationAttribute>())
                {
                    ConsiderType(attribute.Type, runtime, targetAssembly, includedTypes, considerForSerialization: true);
                    if (attribute.ThrowOnFailure && !SerializerGenerationManager.IsTypeRecorded(attribute.Type))
                    {
                        throw new CodeGenerationException(
                            $"Found {attribute.GetType().Name} for type {attribute.Type.GetParseableName()}, but code"
                            + " could not be generated. Ensure that the type is accessible.");
                    }
                }

                KnownAssemblyAttribute knownAssemblyAttribute;
                var considerAllTypesForSerialization = knownAssemblyAttributes.TryGetValue(assembly, out knownAssemblyAttribute)
                                          && knownAssemblyAttribute.TreatTypesAsSerializable;
                foreach (var type in TypeUtils.GetDefinedTypes(assembly, Logger))
                {
                    var considerForSerialization = considerAllTypesForSerialization || type.IsSerializable;
                    ConsiderType(type.AsType(), runtime, targetAssembly, includedTypes, considerForSerialization);
                }
            }

            includedTypes.RemoveWhere(_ => ignoredTypes.Contains(_));

            // Group the types by namespace and generate the required code in each namespace.
            foreach (var group in includedTypes.GroupBy(_ => CodeGeneratorCommon.GetGeneratedNamespace(_)))
            {
                var namespaceMembers = new List<MemberDeclarationSyntax>();
                foreach (var type in group)
                {
                    // The module containing the serializer.
                    var module = runtime ? null : type.GetTypeInfo().Module;

                    // Every type which is encountered must be considered for serialization.
                    Action<Type> onEncounteredType = encounteredType =>
                    {
                        // If a type was encountered which can be accessed, process it for serialization.
                        SerializerGenerationManager.RecordTypeToGenerate(encounteredType, module, targetAssembly);
                    };

                    if (Logger.IsVerbose2)
                    {
                        Logger.Verbose2("Generating code for: {0}", type.GetParseableName());
                    }

                    if (GrainInterfaceUtils.IsGrainInterface(type))
                    {
                        if (Logger.IsVerbose2)
                        {
                            Logger.Verbose2(
                                "Generating GrainReference and MethodInvoker for {0}",
                                type.GetParseableName());
                        }

                        GrainInterfaceUtils.ValidateInterfaceRules(type);

                        namespaceMembers.Add(GrainReferenceGenerator.GenerateClass(type, onEncounteredType));
                        namespaceMembers.Add(GrainMethodInvokerGenerator.GenerateClass(type));
                    }

                    // Generate serializers.
                    var first = true;
                    Type toGen;
                    while (SerializerGenerationManager.GetNextTypeToProcess(out toGen))
                    {
                        if (!runtime)
                        {
                            if (first)
                            {
                                ConsoleText.WriteStatus("ClientGenerator - Generating serializer classes for types:");
                                first = false;
                            }

                            ConsoleText.WriteStatus(
                                "\ttype " + toGen.FullName + " in namespace " + toGen.Namespace
                                + " defined in Assembly " + toGen.GetTypeInfo().Assembly.GetName());
                        }

                        if (Logger.IsVerbose2)
                        {
                            Logger.Verbose2(
                                "Generating & Registering Serializer for Type {0}",
                                toGen.GetParseableName());
                        }

                        namespaceMembers.Add(SerializerGenerator.GenerateClass(toGen, onEncounteredType));
                    }
                }

                if (namespaceMembers.Count == 0)
                {
                    if (Logger.IsVerbose)
                    {
                        Logger.Verbose2("Skipping namespace: {0}", group.Key);
                    }

                    continue;
                }

                members.Add(
                    SF.NamespaceDeclaration(SF.ParseName(group.Key))
                        .AddUsings(
                            TypeUtils.GetNamespaces(typeof(TaskUtility), typeof(GrainExtensions), typeof(IntrospectionExtensions))
                                .Select(_ => SF.UsingDirective(SF.ParseName(_)))
                                .ToArray())
                        .AddMembers(namespaceMembers.ToArray()));
            }

            return new GeneratedSyntax
            {
                SourceAssemblies = assemblies,
                Syntax = members.Count > 0 ? SF.CompilationUnit().AddMembers(members.ToArray()) : null
            };
        }

        private static void ConsiderType(
            Type type,
            bool runtime,
            Assembly targetAssembly,
            ISet<Type> includedTypes,
            bool considerForSerialization = false)
        {
            // The module containing the serializer.
            var typeInfo = type.GetTypeInfo();
            var module = runtime || !Equals(typeInfo.Assembly, targetAssembly) ? null : typeInfo.Module;

            // If a type was encountered which can be accessed and is marked as [Serializable], process it for serialization.
            if (considerForSerialization)
            {
                RecordType(type, module, targetAssembly, includedTypes);
            }
            
            // Consider generic arguments to base types and implemented interfaces for code generation.
            ConsiderGenericBaseTypeArguments(typeInfo, module, targetAssembly, includedTypes);
            ConsiderGenericInterfacesArguments(typeInfo, module, targetAssembly, includedTypes);
            
            // Include grain interface types.
            if (GrainInterfaceUtils.IsGrainInterface(type))
            {
                // If code generation is being performed at runtime, the interface must be accessible to the generated code.
                if (!runtime || TypeUtilities.IsAccessibleFromAssembly(type, targetAssembly))
                {
                    if (Logger.IsVerbose2) Logger.Verbose2("Will generate code for: {0}", type.GetParseableName());

                    includedTypes.Add(type);
                }
            }
        }

        private static void RecordType(Type type, Module module, Assembly targetAssembly, ISet<Type> includedTypes)
        {
            if (SerializerGenerationManager.RecordTypeToGenerate(type, module, targetAssembly))
            {
                includedTypes.Add(type);
            }
        }

        private static void ConsiderGenericBaseTypeArguments(
            TypeInfo typeInfo,
            Module module,
            Assembly targetAssembly,
            ISet<Type> includedTypes)
        {
            if (typeInfo.BaseType == null) return;
            if (!typeInfo.BaseType.IsConstructedGenericType) return;

            foreach (var type in typeInfo.BaseType.GetGenericArguments())
            {
                RecordType(type, module, targetAssembly, includedTypes);
            }
        }

        private static void ConsiderGenericInterfacesArguments(
            TypeInfo typeInfo,
            Module module,
            Assembly targetAssembly,
            ISet<Type> includedTypes)
        {
            var interfaces = typeInfo.GetInterfaces().Where(x => x.IsConstructedGenericType);
            foreach (var type in interfaces.SelectMany(v => v.GetTypeInfo().GetGenericArguments()))
            {
                RecordType(type, module, targetAssembly, includedTypes);
            }
        }

        /// <summary>
        /// Get types which have corresponding generated classes.
        /// </summary>
        /// <returns>Types which have corresponding generated classes marked.</returns>
        private static HashSet<Type> GetTypesWithGeneratedSupportClasses()
        {
            // Get assemblies which contain generated code.
            var all =
                AppDomain.CurrentDomain.GetAssemblies()
                    .Where(assemblies => assemblies.GetCustomAttribute<GeneratedCodeAttribute>() != null)
                    .SelectMany(assembly => TypeUtils.GetDefinedTypes(assembly, Logger));

            // Get all generated types in each assembly.
            var attributes = all.SelectMany(_ => _.GetCustomAttributes<GeneratedAttribute>());
            var results = new HashSet<Type>();
            foreach (var attribute in attributes)
            {
                if (attribute.TargetType != null)
                {
                    results.Add(attribute.TargetType);
                }
            }

            return results;
        }

        /// <summary>
        /// Returns a value indicating whether or not code should be generated for the provided assembly.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <returns>A value indicating whether or not code should be generated for the provided assembly.</returns>
        private static bool ShouldGenerateCodeForAssembly(Assembly assembly)
        {
            return !assembly.IsDynamic && !CompiledAssemblies.ContainsKey(assembly.GetName().FullName)
                   && TypeUtils.IsOrleansOrReferencesOrleans(assembly)
                   && assembly.GetCustomAttribute<GeneratedCodeAttribute>() == null
                   && assembly.GetCustomAttribute<SkipCodeGenerationAttribute>() == null;
        }

        /// <summary>
        /// Registers the input assembly with this class.
        /// </summary>
        /// <param name="input">The assembly to register.</param>
        private static void RegisterGeneratedCodeTargets(Assembly input)
        {
            var targets = input.GetCustomAttributes<OrleansCodeGenerationTargetAttribute>();
            foreach (var target in targets)
            {
                CompiledAssemblies.TryAdd(target.AssemblyName, new CachedAssembly { Loaded = true });
            }
        }
        
        [Serializable]
        private class CachedAssembly : GeneratedAssembly
        {
            public CachedAssembly()
            {
            }

            public CachedAssembly(GeneratedAssembly generated) : base(generated)
            {
            }

            /// <summary>
            /// Gets or sets a value indicating whether or not the assembly has been loaded.
            /// </summary>
            public bool Loaded { get; set; }
        }
    }
}
