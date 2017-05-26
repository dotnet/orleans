using System.Reflection;
using System.Runtime.InteropServices;
using Orleans.CodeGeneration;

#if !EXCLUDE_ASSEMBLYINFO // TODO remove after source tree merge

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("TestServiceFabric")]
[assembly: AssemblyDescription("")]
[assembly: AssemblyConfiguration("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("b93b3853-fd54-4615-891a-9afa3d9d32a2")]

#endif

[assembly: SkipCodeGeneration]
