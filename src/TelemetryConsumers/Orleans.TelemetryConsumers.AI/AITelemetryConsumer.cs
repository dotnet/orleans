using System;
using System.Collections.Generic;
using Microsoft.ApplicationInsights;
using Microsoft.ApplicationInsights.Extensibility;
using Microsoft.Extensions.Options;
using Orleans.Configuration;
using Orleans.Runtime;

namespace Orleans.TelemetryConsumers.AI
{
    /// <summary>
    /// Telemetry consumer class for ApplicationInsights.
    /// </summary>
    public class AITelemetryConsumer : ITraceTelemetryConsumer, IEventTelemetryConsumer, IExceptionTelemetryConsumer,
        IDependencyTelemetryConsumer, IMetricTelemetryConsumer, IRequestTelemetryConsumer
    {
        private readonly TelemetryClient _client;

        /// <summary>
        /// Constructor.
        /// </summary>
        /// <param name="options">The instrumentation key for ApplicationInsights.</param>
        public AITelemetryConsumer(IOptions<ApplicationInsightsTelemetryConsumerOptions> options)
        {
            var telemetryConfiguration = options.Value.TelemetryConfiguration;
            var instrumentationKey = options.Value.InstrumentationKey;
            if (telemetryConfiguration != null)
            {
                this._client = new TelemetryClient(telemetryConfiguration);
            }
            else
            {
                this._client = instrumentationKey != null
                    ? new TelemetryClient(new TelemetryConfiguration { InstrumentationKey = instrumentationKey })
#pragma warning disable CS0618 // Type or member is obsolete
                : new TelemetryClient(Microsoft.ApplicationInsights.Extensibility.TelemetryConfiguration.Active);
#pragma warning restore CS0618 // Type or member is obsolete
            }
        }

        /// <inheritdoc />
        public virtual void DecrementMetric(string name) =>
            this._client.TrackMetric(name, -1, null);

        /// <inheritdoc />
        public virtual void DecrementMetric(string name, double value) =>
            this._client.TrackMetric(name, value * -1, null);

        /// <inheritdoc />
        public virtual void IncrementMetric(string name) =>
            this._client.TrackMetric(name, 1, null);

        /// <inheritdoc />
        public virtual void IncrementMetric(string name, double value) =>
            this._client.TrackMetric(name, value, null);

        /// <inheritdoc />
        public virtual void TrackDependency(string dependencyName, string commandName, DateTimeOffset startTime, TimeSpan duration, bool success) =>
            this._client.TrackDependency("Orleans", dependencyName, commandName, startTime, duration, success);

        /// <inheritdoc />
        public virtual void TrackEvent(string eventName, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null) =>
            this._client.TrackEvent(eventName, properties, metrics);

        /// <inheritdoc />
        public virtual void TrackException(Exception exception, IDictionary<string, string> properties = null, IDictionary<string, double> metrics = null) =>
            this._client.TrackException(exception, properties, metrics);

        /// <inheritdoc />
        public virtual void TrackMetric(string name, TimeSpan value, IDictionary<string, string> properties = null) =>
            this._client.TrackMetric(name, value.TotalMilliseconds, properties);

        /// <inheritdoc />
        public virtual void TrackMetric(string name, double value, IDictionary<string, string> properties = null) =>
            this._client.TrackMetric(name, value, properties);

        /// <inheritdoc />
        public virtual void TrackRequest(string name, DateTimeOffset startTime, TimeSpan duration, string responseCode, bool success) =>
            this._client.TrackRequest(name, startTime, duration, responseCode, success);

        /// <inheritdoc />
        public virtual void TrackTrace(string message) => this.TrackTrace(message, null);

        /// <inheritdoc />
        public virtual void TrackTrace(string message, IDictionary<string, string> properties)
        {
            if (properties != null)
            {
                this._client.TrackTrace(message, Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Information, properties);
            }
            else
            {
                this._client.TrackTrace(message);
            }
        }

        /// <inheritdoc />
        public virtual void TrackTrace(string message, Severity severity) =>
            this.TrackTrace(message, severity, null);

        /// <inheritdoc />
        public virtual void TrackTrace(string message, Severity severity, IDictionary<string, string> properties)
        {
            Microsoft.ApplicationInsights.DataContracts.SeverityLevel sev;

            switch (severity)
            {
                case Severity.Off:
                    return;
                case Severity.Error:
                    sev = Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Error;
                    break;
                case Severity.Warning:
                    sev = Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Warning;
                    break;
                case Severity.Verbose:
                case Severity.Verbose2:
                case Severity.Verbose3:
                    sev = Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Verbose;
                    break;
                default:
                    sev = Microsoft.ApplicationInsights.DataContracts.SeverityLevel.Information;
                    break;
            }

            if (properties == null)
            {
                this._client.TrackTrace(message, sev);
            }
            else
            {
                this._client.TrackTrace(message, sev, properties);
            }
        }

        /// <inheritdoc />
        public virtual void Flush() { }

        /// <inheritdoc />
        public virtual void Close() { }
    }
}
