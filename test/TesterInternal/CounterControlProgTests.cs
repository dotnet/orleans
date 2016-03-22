using Assert = Microsoft.VisualStudio.TestTools.UnitTesting.Assert;
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
            
            Assert.IsTrue(prog.ParseArguments(new string[] { "/r" }));
            Assert.IsFalse(prog.Unregister);
            Assert.IsTrue(prog.ParseArguments(new string[] { "/register" }));
            Assert.IsFalse(prog.Unregister);

            Assert.IsTrue(prog.ParseArguments(new string[] { "/u" }));
            Assert.IsTrue(prog.Unregister);
            Assert.IsTrue(prog.ParseArguments(new string[] { "/unregister" }));
            Assert.IsTrue(prog.Unregister);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void ParseUsageArguments()
        {
            CounterControl prog = new CounterControl();
            Assert.IsFalse(prog.ParseArguments(new string[] { "/?" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "/help" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "/?", "/r", "/u" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "/r", "/u", "/?" }));
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void ParseBadArguments()
        {
            CounterControl prog = new CounterControl();
            Assert.IsFalse(prog.ParseArguments(new string[] { "/xyz" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "/xyz", "/r", "/u" }));
            Assert.IsFalse(prog.ParseArguments(new string[] { "/r", "/u", "/xyz" }));
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void ParseMultipleArgs()
        {
            CounterControl prog = new CounterControl();

            Assert.IsTrue(prog.ParseArguments(new string[] { "/r", "/u" }));
            Assert.IsTrue(prog.Unregister);

            // Last arg wins
            Assert.IsTrue(prog.ParseArguments(new string[] { "/u", "/r" }));
            Assert.IsFalse(prog.Unregister);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void ParseNoArgs()
        {
            CounterControl prog = new CounterControl();
            Assert.IsTrue(prog.ParseArguments(new string[] { }));
            Assert.IsFalse(prog.Unregister);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void NeedsRunAsAdminForRegisterCommand()
        {
            CounterControl prog = new CounterControl();
            prog.ParseArguments(new string[] { "/r" });
            Assert.IsTrue(prog.NeedRunAsAdministrator);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void NeedsRunAsAdminForUnregisterCommand()
        {
            CounterControl prog = new CounterControl();
            prog.ParseArguments(new string[] { "/u" });
            Assert.IsTrue(prog.NeedRunAsAdministrator);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void DoNotNeedsRunAsAdminForOtherCommand()
        {
            CounterControl prog = new CounterControl();
            prog.ParseArguments(new string[] { "/?" });
            Assert.IsFalse(prog.NeedRunAsAdministrator);
        }

        [Fact, TestCategory("Functional"), TestCategory("Management")]
        public void DoNotNeedsRunAsAdminForUnknownCommand()
        {
            CounterControl prog = new CounterControl();
            prog.ParseArguments(new string[] { "/xyz" });
            Assert.IsFalse(prog.NeedRunAsAdministrator);
        }
    }
}
