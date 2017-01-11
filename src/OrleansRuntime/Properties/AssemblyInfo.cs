using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("OrleansRuntime")]
[assembly: AssemblyDescription("Orleans - Runtime")]
[assembly: AssemblyConfiguration("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("6a4bb086-27f3-4522-af76-c6f78c73247b")]

[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: InternalsVisibleTo("LoadTestGrains")]
[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("OrleansCounterControl")]
[assembly: InternalsVisibleTo("OrleansTelemetryConsumers.Counters")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("TestInternalGrains")]
[assembly: InternalsVisibleTo("Orleans.NonSiloTests")]
[assembly: InternalsVisibleTo("OrleansTestingHost")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.SQLUtils")]
[assembly: InternalsVisibleTo("AWSUtils.Tests")]
