namespace TestServiceFabric
{
    using System;
    using System.Collections.Generic;

    using Orleans.Runtime;

    using Xunit.Abstractions;

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

        public override void TrackDependency(string name, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success)
        {
        }

        public override void TrackEvent(string name, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
        }

        public override void TrackMetric(string name, double value, IDictionary<string, string> properties = null)
        {
        }

        public override void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null)
        {
        }

        public override void IncrementMetric(string name)
        {
        }

        public override void IncrementMetric(string name, double value)
        {
        }

        public override void DecrementMetric(string name)
        {
        }

        public override void DecrementMetric(string name, double value)
        {
        }

        public override void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success)
        {
        }

        public override void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null)
        {
        }

        public override void TrackTrace(string message)
        {
        }

        public override void TrackTrace(string message, Severity severityLevel)
        {
        }

        public override void TrackTrace(string message, Severity severityLevel, IDictionary<string, string> properties)
        {
        }

        public override void TrackTrace(string message, IDictionary<string, string> properties)
        {
        }
    }
}