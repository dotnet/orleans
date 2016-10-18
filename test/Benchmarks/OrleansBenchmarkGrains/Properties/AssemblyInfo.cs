using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.CodeGeneration;
using OrleansBenchmarkGrains.MapReduce;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("BenchmarkGrains")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("071cb148-eaa3-4f85-a1f7-52d1b7fdaf82")]

[assembly: GenerateSerializer(typeof(MapProcessor))]
[assembly: GenerateSerializer(typeof(ReduceProcessor))]
[assembly: GenerateSerializer(typeof(EmptyProcessor))]