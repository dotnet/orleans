using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.CodeGeneration;
using Orleans.Providers.Streams.AzureQueue;

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("OrleansAzureUtils")]
[assembly: AssemblyDescription("Orleans - Windows Azure Helper Classes")]
[assembly: AssemblyConfiguration("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("e0846a49-0ddc-4c53-ab74-364c269879a5")]

[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("TesterInternal")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: InternalsVisibleTo("Orleans.NonSiloTests")]
[assembly: InternalsVisibleTo("Tester.AzureUtils")]
[assembly: GenerateSerializer(typeof(AzureQueueBatchContainerV2))]

