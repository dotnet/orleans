using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Placement.Rebalancing;

internal partial class ActivationRebalancerWorker
{
    [LoggerMessage(Level = LogLevel.Trace, Message = "Activation rebalancer has been scheduled to start after {DueTime}.")]
    private partial void LogScheduledToStart(TimeSpan dueTime);

    [LoggerMessage(Level = LogLevel.Trace, Message = "I have started a new rebalancing session.")]
    private partial void LogSessionStarted();

    [LoggerMessage(Level = LogLevel.Trace, Message = "I have stopped my current rebalancing session.")]
    private partial void LogSessionStopped();

    [LoggerMessage(Level = LogLevel.Trace, Message = "I have been told to suspend rebalancing indefinitely.")]
    private partial void LogSuspended();

    [LoggerMessage(Level = LogLevel.Trace, Message = "I have been told to suspend rebalancing for {Duration}.")]
    private partial void LogSuspendedFor(TimeSpan duration);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Can not continue with rebalancing because there are less than 2 silos.")]
    private partial void LogNotEnoughSilos();

    [LoggerMessage(Level = LogLevel.Trace, Message = "Can not continue with rebalancing because I have statistics information for less than 2 silos.")]
    private partial void LogNotEnoughStatistics();

    [LoggerMessage(Level = LogLevel.Warning, Message =
        "Can not continue with rebalancing because at least one of the silos is reporting 0 memory usage. " +
        "This can indicated that there is no implementation of {ProviderName}")]
    private partial void LogInvalidSiloMemoryUsage(string providerName);

    [LoggerMessage(Level = LogLevel.Trace, Message = "The current rebalancing session has stopped due to {StaleCycles} stale cycles having passed, which is the maximum allowed.")]
    private partial void LogMaxStaleCyclesReached(int staleCycles);

    [LoggerMessage(Level = LogLevel.Trace, Message =
        "The current rebalancing session has stopped due to a {EntropyDeviation} " +
        "entropy deviation between the current {CurrentEntropy} and maximum possible {MaximumEntropy}. " +
        "The difference is less than the required {AllowedEntropyDeviation} deviation.")]
    private partial void LogMaxEntropyDeviationReached(double entropyDeviation, double currentEntropy, double maximumEntropy, double allowedEntropyDeviation);

    [LoggerMessage(Level = LogLevel.Trace, Message =
        "The relative change in entropy {EntropyChange} is less than the quantum {EntropyQuantum}. " +
        "This is practically not considered an improvement, therefor this cycle will be marked as stale.")]
    private partial void LogInsufficientEntropyQuantum(double entropyChange, double entropyQuantum);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Stale cycle count has been reset as we are improving now.")]
    private partial void LogStaleCyclesReset();

    [LoggerMessage(Level = LogLevel.Trace, Message = "Failed session count has been reset as we are improving now.")]
    private partial void LogFailedSessionsReset();

    [LoggerMessage(Level = LogLevel.Trace, Message =
        "I have decided to migrate {Delta} activations.\n" +
        "Adjusted activations for {LowSilo} will be [{LowSiloPreActivations} -> {LowSiloPostActivations}].\n" +
        "Adjusted activations for {HighSilo} will be [{HighSiloPreActivations} -> {HighSiloPostActivations}].")]
    private partial void LogSiloMigrations(int delta,
        SiloAddress lowSilo, int lowSiloPreActivations, int lowSiloPostActivations,
        SiloAddress highSilo, int highSiloPreActivations, int highSiloPostActivations);

    [LoggerMessage(Level = LogLevel.Trace, Message =
        "Rebalancing cycle {RebalancingCycle} has finished. " +
        "[ Stale Cycles: {StaleCycles} | Previous Entropy: {PreviousEntropy} | " +
        "Current Entropy: {CurrentEntropy} | Maximum Entropy: {MaximumEntropy} | Entropy Deviation: {EntropyDeviation} ]")]
    private partial void LogCycleOutcome(
        int rebalancingCycle, int staleCycles, double previousEntropy,
        double currentEntropy, double maximumEntropy, double entropyDeviation);
}