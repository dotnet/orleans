
using Orleans.Counter.Control;
using Xunit;

namespace UnitTests
{
    public class CounterControlProgTests
    {
        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void ParseArguments()
        {
            CounterControl prog = new CounterControl();
            
            Assert.True(prog.ParseArguments(new string[] { "/r" }));
            Assert.False(prog.Unregister);
            Assert.True(prog.ParseArguments(new string[] { "/register" }));
            Assert.False(prog.Unregister);

            Assert.True(prog.ParseArguments(new string[] { "/u" }));
            Assert.True(prog.Unregister);
            Assert.True(prog.ParseArguments(new string[] { "/unregister" }));
            Assert.True(prog.Unregister);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void ParseUsageArguments()
        {
            CounterControl prog = new CounterControl();
            Assert.False(prog.ParseArguments(new string[] { "/?" }));
            Assert.False(prog.ParseArguments(new string[] { "/help" }));
            Assert.False(prog.ParseArguments(new string[] { "/?", "/r", "/u" }));
            Assert.False(prog.ParseArguments(new string[] { "/r", "/u", "/?" }));
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void ParseBadArguments()
        {
            CounterControl prog = new CounterControl();
            Assert.False(prog.ParseArguments(new string[] { "/xyz" }));
            Assert.False(prog.ParseArguments(new string[] { "/xyz", "/r", "/u" }));
            Assert.False(prog.ParseArguments(new string[] { "/r", "/u", "/xyz" }));
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void ParseMultipleArgs()
        {
            CounterControl prog = new CounterControl();

            Assert.True(prog.ParseArguments(new string[] { "/r", "/u" }));
            Assert.True(prog.Unregister);

            // Last arg wins
            Assert.True(prog.ParseArguments(new string[] { "/u", "/r" }));
            Assert.False(prog.Unregister);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void ParseNoArgs()
        {
            CounterControl prog = new CounterControl();
            Assert.True(prog.ParseArguments(new string[] { }));
            Assert.False(prog.Unregister);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void NeedsRunAsAdminForRegisterCommand()
        {
            CounterControl prog = new CounterControl();
            prog.ParseArguments(new string[] { "/r" });
            Assert.True(prog.NeedRunAsAdministrator);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void NeedsRunAsAdminForUnregisterCommand()
        {
            CounterControl prog = new CounterControl();
            prog.ParseArguments(new string[] { "/u" });
            Assert.True(prog.NeedRunAsAdministrator);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void DoNotNeedsRunAsAdminForOtherCommand()
        {
            CounterControl prog = new CounterControl();
            prog.ParseArguments(new string[] { "/?" });
            Assert.False(prog.NeedRunAsAdministrator);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void DoNotNeedsRunAsAdminForUnknownCommand()
        {
            CounterControl prog = new CounterControl();
            prog.ParseArguments(new string[] { "/xyz" });
            Assert.False(prog.NeedRunAsAdministrator);
        }
    }
}
