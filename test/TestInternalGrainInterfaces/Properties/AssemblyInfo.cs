using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
#if !EXCLUDEFSHARP
using Microsoft.FSharp.Core;
#endif
using Orleans.CodeGeneration;

#if !EXCLUDE_ASSEMBLYINFO // TODO remove after source tree merge

// General Information about an assembly is controlled through the following
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("TestInternalGrainInterfaces")]

// Setting ComVisible to false makes the types in this assembly not visible
// to COM components.  If you need to access a type in this assembly from
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("314454e5-b572-40aa-9c3e-4ebf7d456c0b")]

#endif

[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("Tester")]
[assembly: InternalsVisibleTo("DefaultCluster.Tests")]
[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("UnitTestGrainInterfaces")]
[assembly: InternalsVisibleTo("UnitTestGrains")]

// generate Orleans serializers for types in FSharp.core.dll
#if !EXCLUDEFSHARP
[assembly: KnownAssembly(typeof(FSharpOption<>))]
#endif