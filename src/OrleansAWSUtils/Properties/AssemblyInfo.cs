using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Orleans.CodeGeneration;

#if !EXCLUDE_ASSEMBLYINFO // TODO remove after source tree merge

// General Information about an assembly is controlled through the following 
// set of attributes. Change these attribute values to modify the information
// associated with an assembly.
[assembly: AssemblyTitle("OrleansAWSUtils")]
[assembly: AssemblyDescription("Orleans - Windows AWS Helper Classes")]
[assembly: AssemblyConfiguration("")]

// Setting ComVisible to false makes the types in this assembly not visible 
// to COM components.  If you need to access a type in this assembly from 
// COM, set the ComVisible attribute to true on that type.
[assembly: ComVisible(false)]

// The following GUID is for the ID of the typelib if this project is exposed to COM
[assembly: Guid("67738e6c-f292-46a2-994d-5b52e745205b")]

#endif

[assembly: InternalsVisibleTo("UnitTests")]
[assembly: InternalsVisibleTo("AWSUtils.Tests")]
[assembly: InternalsVisibleTo("UnitTestGrains")]
[assembly: SkipCodeGeneration]

