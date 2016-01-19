using Microsoft.VisualStudio.TestTools.UnitTesting;
using Orleans.TestingHost;

namespace UnitTests
{
    [TestClass]
    public sealed class TestAssemblyCleanup
    {
        [AssemblyCleanup]
        public static void AssemblyCleanup()
        {
            // Big issue with this! This method gets called not when it transitions to a new assembly, but after all assemblies finish testing.
            // It's an issue because each assembly is run in a separate AppDomain, so once the next assembly starts, it will actually
            // get TestingSiloHost.Instance and it will be null. Nevertheless, the another host might be running, and if they use the same ports then they'll be borked.
            TestingSiloHost.StopAllSilosIfRunning();
        }
    }
}
