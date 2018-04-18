using Microsoft.Extensions.Logging;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Orleans.CodeGeneration;
using Orleans.Runtime;
using Orleans.CodeGenerator.Utilities;
using Orleans.Serialization;
using Orleans.Utilities;
using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;
using Orleans.ApplicationParts;
using Orleans.Metadata;
using GrainInterfaceUtils = Orleans.CodeGeneration.GrainInterfaceUtils;
using SF = Microsoft.CodeAnalysis.CSharp.SyntaxFactory;

namespace Orleans.CodeGenerator
{
    /// <summary>
    /// Implements a code generator using the Roslyn C# compiler.
    /// </summary>
    public class RoslynCodeGenerator
    {
        private const string SerializerNamespacePrefix = "OrleansGeneratedCode";

        /// <summary>
        /// The logger.
        /// </summary>
        private readonly ILogger logger;

        /// <summary>
        /// The serializer generation manager.
        /// </summary>
        private readonly SerializerGenerationManager serializableTypes;

        private readonly TypeCollector typeCollector = new TypeCollector();
        private readonly HashSet<string> knownTypes;
        private readonly HashSet<Type> knownGrainTypes;

        /// <summary>
        /// Initializes a new instance of the <see cref="RoslynCodeGenerator"/> class.
        /// </summary>
        /// <param name="partManager"></param>
        /// <param name="loggerFactory">The logger factory.</param>
        public RoslynCodeGenerator(IApplicationPartManager partManager, ILoggerFactory loggerFactory)
        {
            var serializerFeature = partManager.CreateAndPopulateFeature<SerializerFeature>();
            var grainClassFeature = partManager.CreateAndPopulateFeature<GrainClassFeature>();
            var grainInterfaceFeature = partManager.CreateAndPopulateFeature<GrainInterfaceFeature>();

            this.knownTypes = GetKnownTypes();
            this.serializableTypes = new SerializerGenerationManager(GetExistingSerializers(), loggerFactory);
            this.logger = loggerFactory.CreateLogger<RoslynCodeGenerator>();

            var knownInterfaces = grainInterfaceFeature.Interfaces.Select(i => i.InterfaceType);
            var knownClasses = grainClassFeature.Classes.Select(c => c.ClassType);
            this.knownGrainTypes = new HashSet<Type>(knownInterfaces.Concat(knownClasses));

            HashSet<string> GetKnownTypes()
            {
                var result = new HashSet<string>();
                foreach (var kt in serializerFeature.KnownTypes) result.Add(kt.Type);
                foreach (var serializer in serializerFeature.SerializerTypes)
                {
                    result.Add(RuntimeTypeNameFormatter.Format(serializer.Target));
                    result.Add(RuntimeTypeNameFormatter.Format(serializer.Serializer));
                }

                foreach (var serializer in serializerFeature.SerializerDelegates)
                {
                    result.Add(RuntimeTypeNameFormatter.Format(serializer.Target));
                }

                return result;
            }
            
            HashSet<Type> GetExistingSerializers()
            {
                var result = new HashSet<Type>();
                foreach (var serializer in serializerFeature.SerializerDelegates)
                {
                    result.Add(serializer.Target);
                }

                foreach (var serializer in serializerFeature.SerializerTypes)
                {
                    result.Add(serializer.Target);
                }

                return result;
            }
        }

        /// <summary>
        /// Generates, compiles, and loads the 
        /// </summary>
        /// <param name="assemblies">
        /// The assemblies to generate code for.
        /// </param>
        public Assembly GenerateAndLoadForAssemblies(IEnumerable<Assembly> assemblies)
        {
            var assemblyList = assemblies.Where(ShouldGenerateCodeForAssembly).ToList();
            try
            {
                var timer = Stopwatch.StartNew();
                var generated = this.GenerateCode(targetAssembly: null, assemblies: assemblyList);

                Assembly generatedAssembly;
                if (generated.Syntax != null)
                {
                    var emitDebugSymbols = assemblyList.Any(RuntimeVersion.IsAssemblyDebugBuild);
                    generatedAssembly = this.CompileAssembly(generated, "OrleansCodeGen", emitDebugSymbols);
                }
                else
                {
                    generatedAssembly = null;
                }

                if (this.logger.IsEnabled(LogLevel.Debug))
                {
                    this.logger.Debug(
                        ErrorCode.CodeGenCompilationSucceeded,
                        "Generated code for 1 assembly in {0}ms",
                        timer.ElapsedMilliseconds);
                }

                return generatedAssembly;
            }
            catch (Exception exception)
            {
                var message =
                    $"Exception generating code for input assemblies {string.Join(",", assemblyList.Select(asm => asm.GetName().FullName))}\nException: {LogFormatter.PrintException(exception)}";
                this.logger.Warn(ErrorCode.CodeGenCompilationFailed, message, exception);
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
            if (input.GetCustomAttribute<GeneratedCodeAttribute>() != null
                || input.GetCustomAttribute<SkipCodeGenerationAttribute>() != null)
            {
                return string.Empty;
            }

            var generated = this.GenerateCode(input, new[] { input }.ToList());
            if (generated.Syntax == null)
            {
                return string.Empty;
            }

            return CodeGeneratorCommon.GenerateSourceCode(CodeGeneratorCommon.AddGeneratedCodeAttribute(generated));
        }

        /// <summary>
        /// Generates a syntax tree for the provided assemblies.
        /// </summary>
        /// <param name="targetAssembly">The assemblies used for accessiblity checks, or <see langword="null"/> during runtime code generation.</param>
        /// <param name="assemblies">The assemblies to generate code for.</param>
        /// <returns>The generated syntax tree.</returns>
        private GeneratedSyntax GenerateCode(Assembly targetAssembly, List<Assembly> assemblies)
        {
            var features = new FeatureDescriptions();
            var members = new List<MemberDeclarationSyntax>();

            // Expand the list of included assemblies and types.
            var knownAssemblies =
                new Dictionary<Assembly, KnownAssemblyAttribute>(
                    assemblies.ToDictionary(k => k, k => default(KnownAssemblyAttribute)));
            foreach (var attribute in assemblies.SelectMany(asm => asm.GetCustomAttributes<KnownAssemblyAttribute>()))
            {
                knownAssemblies[attribute.Assembly] = attribute;
            }

            if (logger.IsEnabled(LogLevel.Information))
            {
                logger.Info($"Generating code for assemblies: {string.Join(", ", knownAssemblies.Keys.Select(a => a.FullName))}");
            }

            // Get types from assemblies which reference Orleans and are not generated assemblies.
            var grainClasses = new HashSet<Type>();
            var grainInterfaces = new HashSet<Type>();
            foreach (var pair in knownAssemblies)
            {
                var assembly = pair.Key;
                var treatTypesAsSerializable = pair.Value?.TreatTypesAsSerializable ?? false;
                foreach (var type in TypeUtils.GetDefinedTypes(assembly, this.logger))
                {
                    if (treatTypesAsSerializable || type.IsSerializable || TypeHasKnownBase(type))
                    {
                        string logContext = null;
                        if (logger.IsEnabled(LogLevel.Trace))
                        {
                            if (treatTypesAsSerializable)
                                logContext = $"known assembly {assembly.GetName().Name} where 'TreatTypesAsSerializable' = true";
                            else if (type.IsSerializable)
                                logContext = $"known assembly {assembly.GetName().Name} where type is [Serializable]";
                            else if (type.IsSerializable)
                                logContext = $"known assembly {assembly.GetName().Name} where type has known base type.";
                        }

                        serializableTypes.RecordType(type, targetAssembly, logContext);
                    }

                    // Include grain interfaces and classes.
                    var isGrainInterface = GrainInterfaceUtils.IsGrainInterface(type);
                    var isGrainClass = TypeUtils.IsConcreteGrainClass(type);
                    if (isGrainInterface || isGrainClass)
                    {
                        // If code generation is being performed at runtime, the interface must be accessible to the generated code.
                        if (!TypeUtilities.IsAccessibleFromAssembly(type, targetAssembly))
                        {
                            if (this.logger.IsEnabled(LogLevel.Debug))
                            {
                                this.logger.Debug("Skipping inaccessible grain type, {0}", type.GetParseableName());
                            }

                            continue;
                        }

                        // Attempt to generate serializers for grain state classes, i.e, T in Grain<T>.
                        var baseType = type.BaseType;
                        if (baseType != null && baseType.IsConstructedGenericType)
                        {
                            foreach (var arg in baseType.GetGenericArguments())
                            {
                                string logContext = null;
                                if (logger.IsEnabled(LogLevel.Trace)) logContext = "generic base type of " + type.GetLogFormat();
                                this.serializableTypes.RecordType(arg, targetAssembly, logContext);
                            }
                        }

                        // Skip classes generated by this generator.
                        if (IsOrleansGeneratedCode(type))
                        {
                            if (this.logger.IsEnabled(LogLevel.Debug))
                            {
                                this.logger.Debug("Skipping generated grain type, {0}", type.GetParseableName());
                            }

                            continue;
                        }

                        if (this.knownGrainTypes.Contains(type))
                        {
                            if (this.logger.IsEnabled(LogLevel.Debug))
                            {
                                this.logger.Debug("Skipping grain type {0} since it already has generated code.", type.GetParseableName());
                            }

                            continue;
                        }

                        if (isGrainClass)
                        {
                            if (this.logger.IsEnabled(LogLevel.Information))
                            {
                                this.logger.Info("Found grain implementation class: {0}", type.GetParseableName());
                            }

                            grainClasses.Add(type);
                        }

                        if (isGrainInterface)
                        {
                            if (this.logger.IsEnabled(LogLevel.Information))
                            {
                                this.logger.Info("Found grain interface: {0}", type.GetParseableName());
                            }

                            GrainInterfaceUtils.ValidateInterfaceRules(type);

                            grainInterfaces.Add(type);
                        }
                    }
                }
            }
            
            // Group the types by namespace and generate the required code in each namespace.
            foreach (var groupedGrainInterfaces in grainInterfaces.GroupBy(_ => CodeGeneratorCommon.GetGeneratedNamespace(_)))
            {
                var namespaceName = groupedGrainInterfaces.Key;
                var namespaceMembers = new List<MemberDeclarationSyntax>();
                foreach (var grainInterface in groupedGrainInterfaces)
                {
                    var referenceTypeName = GrainReferenceGenerator.GetGeneratedClassName(grainInterface);
                    var invokerTypeName = GrainMethodInvokerGenerator.GetGeneratedClassName(grainInterface);
                    namespaceMembers.Add(
                        GrainReferenceGenerator.GenerateClass(
                            grainInterface,
                            referenceTypeName,
                            encounteredType =>
                            {
                                string logContext = null;
                                if (logger.IsEnabled(LogLevel.Trace)) logContext = "used by grain type " + grainInterface.GetLogFormat();
                                this.serializableTypes.RecordType(encounteredType, targetAssembly, logContext);
                            }));
                    namespaceMembers.Add(GrainMethodInvokerGenerator.GenerateClass(grainInterface, invokerTypeName));
                    var genericTypeSuffix = GetGenericTypeSuffix(grainInterface.GetGenericArguments().Length);
                    features.GrainInterfaces.Add(
                        new GrainInterfaceDescription
                        {
                            Interface = grainInterface.GetTypeSyntax(includeGenericParameters: false),
                            Reference = SF.ParseTypeName(namespaceName + '.' + referenceTypeName + genericTypeSuffix),
                            Invoker = SF.ParseTypeName(namespaceName + '.' + invokerTypeName + genericTypeSuffix),
                            InterfaceId = GrainInterfaceUtils.GetGrainInterfaceId(grainInterface)
                        });
                }

                members.Add(CreateNamespace(namespaceName, namespaceMembers));
            }

            foreach (var type in grainClasses)
            {
                features.GrainClasses.Add(
                    new GrainClassDescription
                    {
                        ClassType = type.GetTypeSyntax(includeGenericParameters: false)
                    });
            }

            // Generate serializers into their own namespace.
            var serializerNamespace = this.GenerateSerializers(targetAssembly, features);
            members.Add(serializerNamespace);
            
            // Add serialization metadata for the types which were encountered.
            this.AddSerializationTypes(features.Serializers, targetAssembly, knownAssemblies.Keys.ToList());

            foreach (var attribute in knownAssemblies.Keys.SelectMany(asm => asm.GetCustomAttributes<ConsiderForCodeGenerationAttribute>()))
            {
                this.serializableTypes.RecordType(attribute.Type, targetAssembly, "[ConsiderForCodeGeneration]");
                if (attribute.ThrowOnFailure && !this.serializableTypes.IsTypeRecorded(attribute.Type) && !this.serializableTypes.IsTypeIgnored(attribute.Type))
                {
                    throw new CodeGenerationException(
                        $"Found {attribute.GetType().Name} for type {attribute.Type.GetParseableName()}, but code" +
                        " could not be generated. Ensure that the type is accessible.");
                }
            }

            // Generate metadata directives for all of the relevant types.
            var (attributeDeclarations, memberDeclarations) = FeaturePopulatorGenerator.GenerateSyntax(targetAssembly, features);
            members.AddRange(memberDeclarations);
            
            var compilationUnit = SF.CompilationUnit().AddAttributeLists(attributeDeclarations.ToArray()).AddMembers(members.ToArray());
            return new GeneratedSyntax
            {
                SourceAssemblies = knownAssemblies.Keys.ToList(),
                Syntax = compilationUnit
            };
        }

        /// <summary>
        /// Returns true if the provided type has a base type which is marked with <see cref="KnownBaseTypeAttribute"/>.
        /// </summary>
        /// <param name="type">The type.</param>
        private static bool TypeHasKnownBase(Type type)
        {
            if (type == null) return false;
            if (type.GetCustomAttribute<KnownBaseTypeAttribute>() != null) return true;
            if (TypeHasKnownBase(type.BaseType)) return true;
            var interfaces = type.GetInterfaces();
            foreach (var iface in interfaces)
            {
                if (TypeHasKnownBase(iface)) return true;
            }

            return false;
        }

        private NamespaceDeclarationSyntax GenerateSerializers(Assembly targetAssembly, FeatureDescriptions features)
        {
            var serializerNamespaceMembers = new List<MemberDeclarationSyntax>();
            var serializerNamespaceName = $"{SerializerNamespacePrefix}{targetAssembly?.GetName().Name.GetHashCode():X}";
            while (this.serializableTypes.GetNextTypeToProcess(out var toGen))
            {
                if (this.logger.IsEnabled(LogLevel.Trace))
                {
                    this.logger.Trace("Generating serializer for type {0}", toGen.GetParseableName());
                }

                var type = toGen;
                var generatedSerializerName = SerializerGenerator.GetGeneratedClassName(toGen);
                serializerNamespaceMembers.Add(SerializerGenerator.GenerateClass(generatedSerializerName, toGen, encounteredType =>
                {
                    string logContext = null;
                    if (logger.IsEnabled(LogLevel.Trace)) logContext = "generated serializer for " + type.GetLogFormat();
                    this.serializableTypes.RecordType(encounteredType, targetAssembly, logContext);
                }));
                var qualifiedSerializerName = serializerNamespaceName + '.' + generatedSerializerName + GetGenericTypeSuffix(toGen.GetGenericArguments().Length);
                features.Serializers.SerializerTypes.Add(
                    new SerializerTypeDescription
                    {
                        Serializer = SF.ParseTypeName(qualifiedSerializerName),
                        Target = toGen.GetTypeSyntax(includeGenericParameters: false)
                    });
            }

            // Add all generated serializers to their own namespace.
            return CreateNamespace(serializerNamespaceName, serializerNamespaceMembers);
        }

        /// <summary>
        /// Adds serialization type descriptions from <paramref name="targetAssembly"/> to <paramref name="serializationTypes"/>.
        /// </summary>
        /// <param name="serializationTypes">The serialization type descriptions.</param>
        /// <param name="targetAssembly">The target assembly for generated code.</param>
        /// <param name="assemblies"></param>
        private void AddSerializationTypes(SerializationTypeDescriptions serializationTypes, Assembly targetAssembly, List<Assembly> assemblies)
        {
            // Only types which exist in assemblies referenced by the target assembly can be referenced.
            var references = new HashSet<string>(
                assemblies.SelectMany(asm =>
                    asm.GetReferencedAssemblies()
                        .Select(referenced => referenced.Name)
                        .Concat(new[] { asm.GetName().Name })));

            bool IsAssemblyReferenced(Type type)
            {
                // If the target doesn't reference this type's assembly, it cannot reference a type within that assembly.
                return references.Contains(type.Assembly.GetName().Name);
            }

            // Visit all types in other assemblies for serialization metadata.
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!references.Contains(assembly.GetName().Name)) continue;
                foreach (var type in TypeUtils.GetDefinedTypes(assembly, this.logger))
                {
                    this.typeCollector.RecordEncounteredType(type);
                }
            }

            // Returns true if a type can be accessed from source and false otherwise.
            bool IsAccessibleType(Type type) => TypeUtilities.IsAccessibleFromAssembly(type, targetAssembly);

            foreach (var type in this.typeCollector.EncounteredTypes)
            {
                // Skip types which can not or should not be referenced.
                if (type.IsGenericParameter) continue;
                if (!IsAssemblyReferenced(type)) continue;
                if (type.IsNestedPrivate) continue;
                if (type.GetCustomAttribute<CompilerGeneratedAttribute>() != null) continue;
                if (IsOrleansGeneratedCode(type)) continue;

                var qualifiedTypeName = RuntimeTypeNameFormatter.Format(type);
                if (this.knownTypes.Contains(qualifiedTypeName)) continue;

                var typeKeyString = type.OrleansTypeKeyString();
                serializationTypes.KnownTypes.Add(new KnownTypeDescription
                {
                    Type = qualifiedTypeName,
                    TypeKey = typeKeyString
                });

                if (this.logger.IsEnabled(LogLevel.Debug))
                {
                    this.logger.Debug(
                        "Found type {0} with type key \"{1}\"",
                        type.GetParseableName(),
                        typeKeyString);
                }

                if (!IsAccessibleType(type)) continue;

                var typeSyntax = type.GetTypeSyntax(includeGenericParameters: false);
                var serializerAttributes = type.GetCustomAttributes<SerializerAttribute>().ToList();
                if (serializerAttributes.Count > 0)
                {
                    // Account for serializer types.
                    foreach (var serializerAttribute in serializerAttributes)
                    {
                        if (!IsAccessibleType(serializerAttribute.TargetType)) continue;

                        if (this.logger.IsEnabled(LogLevel.Information))
                        {
                            this.logger.Info(
                                "Found type {0} is a serializer for type {1}",
                                type.GetParseableName(),
                                serializerAttribute.TargetType.GetParseableName());
                        }

                        serializationTypes.SerializerTypes.Add(
                            new SerializerTypeDescription
                            {
                                Serializer = typeSyntax,
                                Target = serializerAttribute.TargetType.GetTypeSyntax(includeGenericParameters: false)
                            });
                    }
                }
                else
                {
                    // Account for self-serializing types.
                    SerializationManager.GetSerializationMethods(type, out var copier, out var serializer, out var deserializer);
                    if (copier != null || serializer != null || deserializer != null)
                    {
                        if (this.logger.IsEnabled(LogLevel.Information))
                        {
                            this.logger.Info(
                                "Found type {0} is self-serializing.",
                                type.GetParseableName());
                        }

                        serializationTypes.SerializerTypes.Add(
                            new SerializerTypeDescription
                            {
                                Serializer = typeSyntax,
                                Target = typeSyntax
                            });
                    }
                }
            }
        }
        
        /// <summary>
        /// Generates and compiles an assembly for the provided syntax.
        /// </summary>
        /// <param name="generatedSyntax">
        /// The generated code.
        /// </param>
        /// <param name="assemblyName">
        /// The name for the generated assembly.
        /// </param>
        /// <param name="emitDebugSymbols">
        /// Whether or not to emit debug symbols for the generated assembly.
        /// </param>
        /// <returns>
        /// The raw assembly.
        /// </returns>
        /// <exception cref="CodeGenerationException">
        /// An error occurred generating code.
        /// </exception>
        private Assembly CompileAssembly(GeneratedSyntax generatedSyntax, string assemblyName, bool emitDebugSymbols)
        {
            // Add the generated code attribute.
            var code = CodeGeneratorCommon.AddGeneratedCodeAttribute(generatedSyntax);

            // Reference everything which can be referenced.
            var assemblies =
                AppDomain.CurrentDomain.GetAssemblies()
                         .Where(asm => !asm.IsDynamic && !string.IsNullOrWhiteSpace(asm.Location))
                         .Select(asm => MetadataReference.CreateFromFile(asm.Location))
                         .Cast<MetadataReference>()
                         .ToArray();

            // Generate the code.
            var options = new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary);

#if NETSTANDARD2_0
            // CoreFX bug https://github.com/dotnet/corefx/issues/5540 
            // to workaround it, we are calling internal WithTopLevelBinderFlags(BinderFlags.IgnoreCorLibraryDuplicatedTypes) 
            // TODO: this API will be public in the future releases of Roslyn. 
            // This work is tracked in https://github.com/dotnet/roslyn/issues/5855 
            // Once it's public, we should replace the internal reflection API call by the public one. 
            var method = typeof(CSharpCompilationOptions).GetMethod("WithTopLevelBinderFlags", BindingFlags.NonPublic | BindingFlags.Instance);
            // we need to pass BinderFlags.IgnoreCorLibraryDuplicatedTypes, but it's an internal class 
            // http://source.roslyn.io/#Microsoft.CodeAnalysis.CSharp/Binder/BinderFlags.cs,00f268571bb66b73 
            options = (CSharpCompilationOptions)method.Invoke(options, new object[] { 1u << 26 });
#endif

            string source = null;
            if (this.logger.IsEnabled(LogLevel.Debug))
            {
                source = CodeGeneratorCommon.GenerateSourceCode(code);

                // Compile the code and load the generated assembly.
                this.logger.Debug(
                    ErrorCode.CodeGenSourceGenerated,
                    "Generating assembly {0} with source:\n{1}",
                    assemblyName,
                    source);
            }

            var compilation =
                CSharpCompilation.Create(assemblyName)
                                 .AddSyntaxTrees(code.SyntaxTree)
                                 .AddReferences(assemblies)
                                 .WithOptions(options);

            using (var outputStream = new MemoryStream())
            {
                var emitOptions = new EmitOptions()
                    .WithEmitMetadataOnly(false)
                    .WithIncludePrivateMembers(true);

                if (emitDebugSymbols)
                {
                    emitOptions = emitOptions.WithDebugInformationFormat(DebugInformationFormat.Embedded);
                }

                var compilationResult = compilation.Emit(outputStream, options: emitOptions);
                if (!compilationResult.Success)
                {
                    source = source ?? CodeGeneratorCommon.GenerateSourceCode(code);
                    var errors = string.Join("\n", compilationResult.Diagnostics.Select(_ => _.ToString()));
                    this.logger.Warn(
                        ErrorCode.CodeGenCompilationFailed,
                        "Compilation of assembly {0} failed with errors:\n{1}\nGenerated Source Code:\n{2}",
                        assemblyName,
                        errors,
                        source);
                    throw new CodeGenerationException(errors);
                }

                this.logger.Debug(
                    ErrorCode.CodeGenCompilationSucceeded,
                    "Compilation of assembly {0} succeeded.",
                    assemblyName);
                return Assembly.Load(outputStream.ToArray());
            }
        }

        private static string GetGenericTypeSuffix(int numParams)
        {
            if (numParams == 0) return string.Empty;
            return '<' + new string(',', numParams - 1) + '>';
        }

        private static NamespaceDeclarationSyntax CreateNamespace(string namespaceName, IEnumerable<MemberDeclarationSyntax> namespaceMembers)
        {
            return
                SF.NamespaceDeclaration(SF.ParseName(namespaceName))
                  .AddUsings(
                      TypeUtils.GetNamespaces(typeof(GrainExtensions), typeof(IntrospectionExtensions))
                               .Select(_ => SF.UsingDirective(SF.ParseName(_)))
                               .ToArray())
                  .AddMembers(namespaceMembers.ToArray());
        }

        /// <summary>
        /// Returns a value indicating whether or not code should be generated for the provided assembly.
        /// </summary>
        /// <param name="assembly">The assembly.</param>
        /// <returns>A value indicating whether or not code should be generated for the provided assembly.</returns>
        private bool ShouldGenerateCodeForAssembly(Assembly assembly)
        {
            return !assembly.IsDynamic
                   && TypeUtils.IsOrleansOrReferencesOrleans(assembly)
                   && assembly.GetCustomAttribute<GeneratedCodeAttribute>() == null
                   && assembly.GetCustomAttribute<SkipCodeGenerationAttribute>() == null;
        }

        private bool IsOrleansGeneratedCode(MemberInfo type) =>
            string.Equals(type.GetCustomAttribute<GeneratedCodeAttribute>()?.Tool, CodeGeneratorCommon.ToolName, StringComparison.Ordinal);
    }
}
