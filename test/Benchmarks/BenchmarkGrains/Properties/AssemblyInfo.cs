using System.Reflection;
using System.Runtime.InteropServices;
using BenchmarkGrains.MapReduce;
using Orleans.CodeGeneration;

#if !EXCLUDE_ASSEMBLYINFO // TODO remove after source tree merge

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("OrleansBenchmarkGrains")]
// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("071cb148-eaa3-4f85-a1f7-52d1b7fdaf82")]

#endif

[assembly: GenerateSerializer(typeof(MapProcessor))]
[assembly: GenerateSerializer(typeof(ReduceProcessor))]
[assembly: GenerateSerializer(typeof(EmptyProcessor))]