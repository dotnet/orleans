using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.Serialization;
using System.Text;
using Orleans.Runtime;

namespace Orleans.Reminders.TestKit;

/// <summary>
/// The exception thrown when a reminder table implementation violates a documented conformance guarantee.
/// </summary>
[Serializable]
[GenerateSerializer]
public sealed class ReminderConformanceException : Exception
{
    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderConformanceException"/> class.
    /// </summary>
    public ReminderConformanceException() : base("A reminder table conformance guarantee was violated.")
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderConformanceException"/> class.
    /// </summary>
    /// <param name="message">The structured failure report.</param>
    public ReminderConformanceException(string message) : base(message)
    {
    }

    /// <summary>
    /// Initializes a new instance of the <see cref="ReminderConformanceException"/> class.
    /// </summary>
    /// <param name="message">The structured failure report.</param>
    /// <param name="innerException">The underlying exception.</param>
    public ReminderConformanceException(string message, Exception? innerException) : base(message, innerException)
    {
    }

    [Obsolete]
    private ReminderConformanceException(SerializationInfo info, StreamingContext context) : base(info, context)
    {
    }
}

/// <summary>
/// Builds the structured failure report which every reminder conformance failure carries.
/// </summary>
/// <remarks>
/// A report always identifies the provider, the violated guarantee, and the failing operation. It additionally
/// carries, when known: the operation sequence, the reminder identity and its uniform hash code, the expected and
/// observed results, the current, previous and supplied ETags, the hash range and ownership under test, and the
/// schedule or loading-window state at the point of failure.
/// </remarks>
public sealed class ReminderFailureReport
{
    private readonly List<KeyValuePair<string, string>> _details = [];
    private readonly string _provider;
    private readonly string _guarantee;
    private readonly string _operation;

    private ReminderFailureReport(string provider, string guarantee, string operation)
    {
        _provider = string.IsNullOrWhiteSpace(provider) ? "<unnamed-provider>" : provider;
        _guarantee = string.IsNullOrWhiteSpace(guarantee) ? "<unnamed-guarantee>" : guarantee;
        _operation = string.IsNullOrWhiteSpace(operation) ? "<unnamed-operation>" : operation;
    }

    /// <summary>
    /// Creates a report for the specified provider, guarantee and operation.
    /// </summary>
    /// <param name="provider">The provider under test.</param>
    /// <param name="guarantee">The conformance guarantee being verified.</param>
    /// <param name="operation">The <see cref="IReminderTable"/> operation which produced the observation.</param>
    /// <returns>The new report.</returns>
    public static ReminderFailureReport Create(string provider, string guarantee, string operation)
        => new(provider, guarantee, operation);

    /// <summary>
    /// Records the reminder identity and its uniform hash code.
    /// </summary>
    /// <param name="grainId">The grain identifier.</param>
    /// <param name="reminderName">The reminder name.</param>
    /// <returns>This report.</returns>
    public ReminderFailureReport WithIdentity(GrainId grainId, string? reminderName)
        => Add("reminder", $"GrainId={grainId}, ReminderName={Format(reminderName)}, UniformHash={grainId.GetUniformHashCode()} (0x{grainId.GetUniformHashCode():X8})");

    /// <summary>
    /// Records the expected result.
    /// </summary>
    /// <param name="expected">A description of the expected result.</param>
    /// <returns>This report.</returns>
    public ReminderFailureReport WithExpected(string expected) => Add("expected", expected);

    /// <summary>
    /// Records the observed result.
    /// </summary>
    /// <param name="observed">A description of the observed result.</param>
    /// <returns>This report.</returns>
    public ReminderFailureReport WithObserved(string observed) => Add("observed", observed);

    /// <summary>
    /// Records the ETags relevant to the failure.
    /// </summary>
    /// <param name="current">The ETag currently believed to be persisted.</param>
    /// <param name="previous">The ETag replaced by the most recent write.</param>
    /// <param name="supplied">The ETag supplied to the failing operation.</param>
    /// <returns>This report.</returns>
    public ReminderFailureReport WithETags(string? current, string? previous = null, string? supplied = null)
        => Add("etags", $"current={Format(current)}, previous={Format(previous)}, supplied={Format(supplied)}");

    /// <summary>
    /// Records the hash range under test, including whether it wraps around zero.
    /// </summary>
    /// <param name="begin">The exclusive lower bound.</param>
    /// <param name="end">The inclusive upper bound.</param>
    /// <returns>This report.</returns>
    public ReminderFailureReport WithRange(uint begin, uint end)
        => Add("range", $"(begin, end] = ({begin}, {end}], wrapAround={(begin >= end).ToString(CultureInfo.InvariantCulture)}");

    /// <summary>
    /// Records the ownership hash codes relevant to the failure.
    /// </summary>
    /// <param name="label">A label describing the hash set, for example <c>expectedInRange</c>.</param>
    /// <param name="hashes">The uniform hash codes.</param>
    /// <returns>This report.</returns>
    public ReminderFailureReport WithOwnership(string label, IEnumerable<uint> hashes)
        => Add($"ownership.{label}", string.Join(", ", hashes.Select(hash => hash.ToString(CultureInfo.InvariantCulture))));

    /// <summary>
    /// Records the persisted schedule.
    /// </summary>
    /// <param name="startAt">The reminder start time.</param>
    /// <param name="period">The reminder period.</param>
    /// <returns>This report.</returns>
    public ReminderFailureReport WithSchedule(DateTime startAt, TimeSpan period)
        => Add("schedule", $"StartAt={startAt:O} (Kind={startAt.Kind}), Period={period}");

    /// <summary>
    /// Records the time and window state at the point of failure.
    /// </summary>
    /// <param name="now">The current time observed by the test.</param>
    /// <param name="window">The relevant window length.</param>
    /// <returns>This report.</returns>
    public ReminderFailureReport WithWindow(DateTime now, TimeSpan window)
        => Add("window", $"now={now:O}, window={window}, windowEnd={now + window:O}");

    /// <summary>
    /// Records the operation sequence which produced the failure.
    /// </summary>
    /// <param name="position">The one-based position of the failing operation.</param>
    /// <param name="operations">The full operation sequence.</param>
    /// <returns>This report.</returns>
    public ReminderFailureReport WithSequence(int position, IEnumerable<string> operations)
        => Add("sequence", $"#{position} of [{string.Join(" -> ", operations)}]");

    /// <summary>
    /// Records an additional named detail.
    /// </summary>
    /// <param name="name">The detail name.</param>
    /// <param name="value">The detail value.</param>
    /// <returns>This report.</returns>
    public ReminderFailureReport WithDetail(string name, string? value) => Add(name, Format(value));

    /// <summary>
    /// Renders the structured failure report.
    /// </summary>
    /// <returns>The rendered report.</returns>
    public string Build()
    {
        var builder = new StringBuilder();
        builder.Append("Reminder conformance failure [provider=").Append(_provider)
            .Append(", guarantee=").Append(_guarantee)
            .Append(", operation=").Append(_operation)
            .Append(']');

        foreach (var detail in _details)
        {
            builder.AppendLine().Append("  ").Append(detail.Key).Append(": ").Append(detail.Value);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Creates the exception which carries this report.
    /// </summary>
    /// <param name="innerException">An optional underlying exception.</param>
    /// <returns>The exception.</returns>
    public ReminderConformanceException ToException(Exception? innerException = null)
        => new(Build(), innerException);

    /// <summary>
    /// Throws the exception which carries this report.
    /// </summary>
    /// <param name="innerException">An optional underlying exception.</param>
    /// <exception cref="ReminderConformanceException">Always thrown.</exception>
    public void Throw(Exception? innerException = null) => throw ToException(innerException);

    private ReminderFailureReport Add(string name, string value)
    {
        _details.Add(new KeyValuePair<string, string>(name, value));
        return this;
    }

    private static string Format(string? value) => value is null ? "<null>" : $"'{value}'";
}
