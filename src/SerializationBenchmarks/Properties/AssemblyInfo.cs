using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.CodeGeneration;
using SerializationBenchmarks;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("SerializationBenchmarks")]
[assembly: AssemblyProduct("SerializationBenchmarks")]

[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("29a8c5e0-8129-40b9-b57b-5b9a8c6d004e")]

[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: GenerateSerializer(typeof(PocoState))]