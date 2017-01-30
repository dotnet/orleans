using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.CodeGeneration;
using Orleans.Providers.Streams.Common;
using Orleans.Providers.Streams.Generator;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("OrleansProviders")]
[assembly: AssemblyDescription("Orleans - Providers")]
[assembly: AssemblyConfiguration("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("781624d3-58fb-4196-8529-ce7d0fa5c466")]

[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("Orleans.NonSiloTests")]
[assembly: InternalsVisibleTo("TestExtensions")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: InternalsVisibleTo("Tester.SQLUtils")]

[assembly: GenerateSerializer(typeof(EventSequenceTokenV2))]
[assembly: GenerateSerializer(typeof(GeneratedBatchContainer))]
