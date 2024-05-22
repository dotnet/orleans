using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Placement.Rebalancing;

internal partial class ActivationRebalancer
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "I will periodically initiate the exchange protocol every {RebalancingPeriod} starting in {DueTime}.")]
    private partial void LogPeriodicallyInvokeProtocol(TimeSpan rebalancingPeriod, TimeSpan dueTime);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Active rebalancing is enabled, but the cluster contains only one silo. Waiting for at least another silo to join the cluster to proceed.")]
    private partial void LogSingleSiloCluster();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Exchange set for candidate silo {CandidateSilo} is empty. I will try the next best candidate (if one is available), otherwise I will wait for my next period to come.")]
    private partial void LogExchangeSetIsEmpty(SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Beginning exchange protocol between {ThisSilo} and {CandidateSilo}.")]
    private partial void LogBeginningProtocol(SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "I got an exchange request from {SendingSilo}, but I have been recently involved in another exchange {LastExchangeDuration} ago. My recovery period is {RecoveryPeriod}")]
    private partial void LogExchangedRecently(SiloAddress sendingSilo, TimeSpan lastExchangeDuration, TimeSpan recoveryPeriod);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Exchange request from {ThisSilo} failed, due to {CandidateSilo} having been recently involved in another exchange. I will try the next best candidate (if one is available), otherwise I will wait for my next period to come.")]
    private partial void LogExchangedRecentlyResponse(SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "I got an exchange request from {SendingSilo}, but I am performing one with it at the same time. I have phase-shifted my timer to avoid these conflicts.")]
    private partial void LogMutualExchangeAttempt(SiloAddress sendingSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Exchange request from {ThisSilo} superseded by a mutual exchange attempt with {CandidateSilo}.")]
    private partial void LogMutualExchangeAttemptResponse(SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "I have successfully finalized my part of the exchange protocol. It was decided that I will give {GivingActivationCount} activations and take on a total of {TakingActivationCount} activations.")]
    private partial void LogProtocolFinalized(int givingActivationCount, int takingActivationCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "An error occurred while performing exchange request from {ThisSilo} to {CandidateSilo}. I will try the next best candidate (if one is available), otherwise I will wait for my next period to come.")]
    private partial void LogErrorOnProtocolExecution(Exception exception, SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "There was an issue during the migration of the activation set initiated by {ThisSilo}.")]
    private partial void LogErrorOnMigratingActivations(Exception exception, SiloAddress thisSilo);
}