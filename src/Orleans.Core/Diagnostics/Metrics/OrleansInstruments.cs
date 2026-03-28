using System;
using System.Diagnostics.Metrics;

namespace Orleans.Runtime;

/// <summary>
/// Provides the <see cref="Meter"/> used by Orleans runtime metrics.
/// </summary>
/// <param name="meterFactory">The meter factory used to create the Orleans meter.</param>
public class OrleansInstruments(IMeterFactory meterFactory)
{
    /// <summary>
    /// Gets the Orleans runtime meter.
    /// </summary>
    public Meter Meter { get; } = (meterFactory ?? throw new ArgumentNullException(nameof(meterFactory))).Create("Microsoft.Orleans");
}
