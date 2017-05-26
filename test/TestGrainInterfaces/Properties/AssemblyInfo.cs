using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.CodeGeneration;
using UnitTests.GrainInterfaces;

#if !EXCLUDE_ASSEMBLYINFO // TODO remove after source tree merge

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("TestGrainInterfaces")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("15F8D1EB-6A01-408B-81B0-6CF5FD0D190A")]

#endif

[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("DefaultCluster.Tests")]
[assembly: InternalsVisibleTo("NonSilo.Tests")]
[assembly: GenerateSerializer(typeof(SomeTypeDerivedFromTypeUsedInGrainInterface))]
