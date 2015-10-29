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
    using System.Diagnostics;
    using System.Linq;
    using System.Reflection;

    using Microsoft.CodeAnalysis.CSharp.Syntax;

    using Orleans.Async;
    using Orleans.CodeGeneration;
    using Orleans.Runtime;
    using Orleans.Serialization;

    using GrainInterfaceData = Orleans.CodeGeneration.GrainInterfaceData;
    using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

    /// <summary>
    /// Implements a code generator using the Roslyn C# compiler.
    /// </summary>
    public class RoslynCodeGenerator : IRuntimeCodeGenerator, ISourceCodeGenerator
    {
        /// <summary>
        /// The compiled assemblies.
        /// </summary>
        private static readonly ConcurrentDictionary<string, GeneratedAssembly> CompiledAssemblies =
            new ConcurrentDictionary<string, GeneratedAssembly>();

        /// <summary>
        /// The logger.
        /// </summary>
        private static readonly Logger Logger = TraceLogger.GetLogger("CodeGenerator");

        /// <summary>
        /// The static instance.
        /// </summary>
        private static readonly RoslynCodeGenerator StaticInstance = new RoslynCodeGenerator();

        /// <summary>
        /// Initializes static members of the <see cref="RoslynCodeGenerator"/> class.
        /// </summary>
        static RoslynCodeGenerator()
        {
            AppDomain.CurrentDomain.AssemblyLoad += (sender, args) => RegisterGeneratedCodeTarget(args.LoadedAssembly);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                RegisterGeneratedCodeTarget(asm);
            }
        }

        /// <summary>
        /// Gets the static instance.
        /// </summary>
        public static RoslynCodeGenerator Instance
        {
            get
            {
                return StaticInstance;
            }
        }

        /// <summary>
        /// Adds a pre-generated assembly.
        /// </summary>
        /// <param name="assemblyName">
        /// The name of the assembly the provided <paramref name="rawAssembly"/> targets.
        /// </param>
        /// <param name="rawAssembly">
        /// The raw assembly.
        /// </param>
        public static void AddCachedAssembly(string assemblyName, byte[] rawAssembly)
        {
            CompiledAssemblies.TryAdd(assemblyName, new GeneratedAssembly { RawBytes = rawAssembly });
        }

        /// <summary>
        /// Generates code for all loaded assemblies and loads the output.
        /// </summary>
        public void GenerateAndLoadForAllAssemblies()
        {
            this.GenerateAndLoadForAssemblies(AppDomain.CurrentDomain.GetAssemblies());
        }

        /// <summary>
        /// Generates and loads code for the specified inputs.
        /// </summary>
        /// <param name="inputs">The assemblies to generate code for.</param>
        public void GenerateAndLoadForAssemblies(params Assembly[] inputs)
        {
            if (inputs == null)
            {
                throw new ArgumentNullException("inputs");
            }

            var timer = Stopwatch.StartNew();
            foreach (var input in inputs)
            {
                TryLoadGeneratedAssemblyFromCache(input);
            }

            var grainAssemblies = inputs.Where(ShouldGenerateCodeForAssembly).ToList();
            if (grainAssemblies.Count == 0)
            {
                // Already up to date.
                return;
            }

            // Generate code for newly loaded assemblies.
            var generatedSyntax = GenerateForAssemblies(grainAssemblies, true);

            var compiled = default(byte[]);
            if (generatedSyntax.Syntax != null)
            {
                compiled = CompileAndLoad(generatedSyntax);
            }

            foreach (var assembly in generatedSyntax.SourceAssemblies)
            {
                var generatedAssembly = new GeneratedAssembly { Loaded = true, RawBytes = compiled };
                CompiledAssemblies.AddOrUpdate(
                    assembly.GetName().FullName,
                    generatedAssembly,
                    (_, __) => generatedAssembly);
            }

            if (Logger.IsVerbose2)
            {
                Logger.Verbose2(
                    (int)ErrorCode.CodeGenCompilationSucceeded,
                    "Generated code for {0} assemblies in {1}ms",
                    generatedSyntax.SourceAssemblies.Count,
                    timer.ElapsedMilliseconds);
            }
        }

        /// <summary>
        /// Ensures that code generation has been run for the provided assembly.
        /// </summary>
        /// <param name="input">
        /// The assembly to generate code for.
        /// </param>
        public void GenerateAndLoadForAssembly(Assembly input)
        {
            if (!ShouldGenerateCodeForAssembly(input))
            {
                TryLoadGeneratedAssemblyFromCache(input);

                return;
            }

            var timer = Stopwatch.StartNew();
            var generated = GenerateForAssemblies(new List<Assembly> { input }, true);

            var compiled = default(byte[]);
            if (generated.Syntax != null)
            {
                compiled = CompileAndLoad(generated);
            }

            foreach (var assembly in generated.SourceAssemblies)
            {
                var generatedAssembly = new GeneratedAssembly { Loaded = true, RawBytes = compiled };
                CompiledAssemblies.AddOrUpdate(
                    assembly.GetName().FullName,
                    generatedAssembly,
                    (_, __) => generatedAssembly);
            }

            if (Logger.IsVerbose2)
            {
                Logger.Verbose2(
                    (int)ErrorCode.CodeGenCompilationSucceeded,
                    "Generated code for 1 assembly in {0}ms",
                    timer.ElapsedMilliseconds);
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
        internal IDictionary<string, byte[]> GetGeneratedAssemblies()
        {
            return CompiledAssemblies.ToDictionary(_ => _.Key, _ => _.Value.RawBytes);
        }

        /// <summary>
        /// Attempts to load a generated assembly from the cache.
        /// </summary>
        /// <param name="targetAssembly">
        /// The target assembly which the cached counterpart is generated for.
        /// </param>
        private static void TryLoadGeneratedAssemblyFromCache(Assembly targetAssembly)
        {
            GeneratedAssembly cached;
            if (!CompiledAssemblies.TryGetValue(targetAssembly.GetName().FullName, out cached)
                || cached.RawBytes == null || cached.Loaded)
            {
                return;
            }

            // Load the assembly and mark it as being loaded.
            Assembly.Load(cached.RawBytes);
            cached.Loaded = true;
        }

        /// <summary>
        /// Compiles the provided syntax tree, and loads and returns the result.
        /// </summary>
        /// <param name="generatedSyntax">The syntax tree.</param>
        /// <returns>The compilation output.</returns>
        private static byte[] CompileAndLoad(GeneratedSyntax generatedSyntax)
        {
            var rawAssembly = CodeGeneratorCommon.CompileAssembly(generatedSyntax, "OrleansCodeGen.dll");
            Assembly.Load(rawAssembly);
            return rawAssembly;
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
                ignoredTypes = CodeGeneratorCommon.GetTypesWithImplementations(
                    typeof(MethodInvokerAttribute),
                    typeof(GrainReferenceAttribute),
                    typeof(GrainStateAttribute),
                    typeof(SerializerAttribute));
                targetAssembly = null;
            }
            else
            {
                ignoredTypes = new HashSet<Type>();
                targetAssembly = assemblies.FirstOrDefault();
            }

            var members = new List<MemberDeclarationSyntax>();

            // Get types from assemblies which reference Orleans and are not generated assemblies.
            var includedTypes = new HashSet<Type>();
            foreach (var type in assemblies.SelectMany(_ => _.DefinedTypes))
            {
                // The module containing the serializer.
                var module = runtime ? null : type.Module;

                // Every type which is encountered must be considered for serialization.
                if (!type.IsNested && !type.IsGenericParameter && type.IsSerializable)
                {
                    // If a type was encountered which can be accessed, process it for serialization.
                    var isAccessibleForSerialization =
                        !TypeUtilities.IsTypeIsInaccessibleForSerialization(type, module, targetAssembly);
                    if (isAccessibleForSerialization)
                    {
                        includedTypes.Add(type);
                        SerializerGenerationManager.RecordTypeToGenerate(type);
                    }
                }

                // Collect the types which require code generation.
                if (GrainInterfaceData.IsGrainInterface(type))
                {
                    if (Logger.IsVerbose2)
                    {
                        Logger.Verbose2("Will generate code for: {0}", type.GetParseableName());
                    }

                    includedTypes.Add(type);
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
                    var module = runtime ? null : type.Module;

                    // Every type which is encountered must be considered for serialization.
                    Action<Type> onEncounteredType = encounteredType =>
                    {
                        // If a type was encountered which can be accessed, process it for serialization.
                        var isAccessibleForSerialization =
                            !TypeUtilities.IsTypeIsInaccessibleForSerialization(encounteredType, module, targetAssembly);
                        if (isAccessibleForSerialization)
                        {
                            SerializerGenerationManager.RecordTypeToGenerate(encounteredType);
                        }
                    };

                    if (Logger.IsVerbose2)
                    {
                        Logger.Verbose2("Generating code for: {0}", type.GetParseableName());
                    }

                    if (GrainInterfaceData.IsGrainInterface(type))
                    {
                        if (Logger.IsVerbose2)
                        {
                            Logger.Verbose2(
                                "Generating GrainReference and MethodInvoker for {0}",
                                type.GetParseableName());
                        }

                        namespaceMembers.Add(GrainReferenceGenerator.GenerateClass(type, onEncounteredType));
                        namespaceMembers.Add(GrainMethodInvokerGenerator.GenerateClass(type));
                    }

                    // Generate serializers.
                    var first = true;
                    Type toGen;
                    while (SerializerGenerationManager.GetNextTypeToProcess(out toGen))
                    {
                        // Filter types which are inaccessible by the serialzation module/assembly.
                        var skipSerialzerGeneration =
                            toGen.GetAllFields()
                                .Any(
                                    field =>
                                    TypeUtilities.IsTypeIsInaccessibleForSerialization(
                                        field.FieldType,
                                        module,
                                        targetAssembly));
                        if (skipSerialzerGeneration)
                        {
                            continue;
                        }

                        if (!runtime)
                        {
                            if (first)
                            {
                                ConsoleText.WriteStatus("ClientGenerator - Generating serializer classes for types:");
                                first = false;
                            }

                            ConsoleText.WriteStatus(
                                "\ttype " + toGen.FullName + " in namespace " + toGen.Namespace
                                + " defined in Assembly " + toGen.Assembly.GetName());
                        }

                        if (Logger.IsVerbose2)
                        {
                            Logger.Verbose2(
                                "Generating & Registering Serializer for Type {0}",
                                toGen.GetParseableName());
                        }

                        namespaceMembers.AddRange(SerializerGenerator.GenerateClass(toGen, onEncounteredType));
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
                            TypeUtils.GetNamespaces(typeof(TaskUtility), typeof(GrainExtensions))
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
        private static void RegisterGeneratedCodeTarget(Assembly input)
        {
            var targets = input.GetCustomAttributes<OrleansCodeGenerationTargetAttribute>();
            foreach (var target in targets)
            {
                CompiledAssemblies.TryAdd(target.AssemblyName, new GeneratedAssembly { Loaded = true });
            }
        }

        /// <summary>
        /// Represents a generated assembly.
        /// </summary>
        private class GeneratedAssembly
        {
            /// <summary>
            /// Gets or sets a value indicating whether or not the assembly has been loaded.
            /// </summary>
            public bool Loaded { get; set; }

            /// <summary>
            /// Gets or sets a serialized representation of the assembly.
            /// </summary>
            public byte[] RawBytes { get; set; }
        }
    }
}