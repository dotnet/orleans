using System;
using Orleans.Runtime;
using Xunit.Abstractions;

namespace TestServiceFabric
{
    public class TestOutputLogger : Logger {

        public Severity Severity { get; set; }

        public override Severity SeverityLevel => this.Severity;

        public string LoggerName { get; set; }

        public override string Name => this.LoggerName;

        public TestOutputLogger(ITestOutputHelper output, string name = null, Severity severity = Severity.Info)
        {
            this.Output = output;
            this.LoggerName = name ?? nameof(TestOutputLogger);
            this.Severity = severity;
        }

        public ITestOutputHelper Output { get; set; }

        public override Logger GetLogger(string loggerName)
        {
            return new TestOutputLogger(this.Output, loggerName, this.Severity);
        }

        public override void Log(int errorCode, Severity sev, string format, object[] args, Exception exception)
        {
            var errCodeMsg = errorCode == 0 ? string.Empty : $"(0x{errorCode: 8X})";
            this.Output.WriteLine($"{sev} {errCodeMsg} [{this.Name}] {string.Format(format, args)}");
        }
    }
}