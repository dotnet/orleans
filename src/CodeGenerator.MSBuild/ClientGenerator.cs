using Microsoft.Orleans.CodeGenerator.MSBuild;
using Orleans.CodeGenerator;

namespace Orleans.CodeGeneration
{
    using System;
    using System.Collections.Generic;
    using System.IO;
    using System.Reflection;
    using Orleans.Serialization;
    using Orleans.Runtime.Configuration;

    internal class CodeGenOptions
    {
        public FileInfo InputAssembly;

        public List<string> ReferencedAssemblies = new List<string>();

        public string OutputFileName;
    }

    internal class GrainClientGeneratorFlags
    {
        internal static bool Verbose = false;

        internal static bool FailOnPathNotFound = false;
    }

    /// <summary>
    /// Generates factory, grain reference, and invoker classes for grain interfaces.
    /// Generates state object classes for grain implementation classes.
    /// </summary>
    public class GrainClientGenerator
    {
    }
}
