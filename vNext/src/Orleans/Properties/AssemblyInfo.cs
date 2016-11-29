using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("Orleans")]
[assembly: AssemblyDescription("Orleans - Orleans API")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

[assembly: InternalsVisibleTo("ClientGenerator")]
[assembly: InternalsVisibleTo("OrleansCodeGenerator")]
[assembly: InternalsVisibleTo("OrleansRuntime")]
[assembly: InternalsVisibleTo("OrleansTestingHost")]
[assembly: InternalsVisibleTo("Orleans.NonSiloTests")]
[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("TestExtensions")]
[assembly: InternalsVisibleTo("OrleansAzureUtils")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
