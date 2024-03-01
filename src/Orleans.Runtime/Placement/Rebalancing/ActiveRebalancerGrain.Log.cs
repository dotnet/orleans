using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Placement.Rebalancing;

internal partial class ActiveRebalancerGrain
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "I will periodically initiate the exchange protocol every {RebalancingPeriod} starting in {DueTime}.")]
    private partial void LogPeriodicallyInvokeProtocol(TimeSpan rebalancingPeriod, TimeSpan dueTime);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Active rebalancing is enabled, but the cluster contains only one silo. Awaiting for at least another silo to join the cluster to proceed.")]
    private partial void LogSingleSiloCluster();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Exchange set for candidate silo {CandidateSilo} is empty. I will try the next best candidate (if one is available), otherwise I will wait for my next period to come.")]
    private partial void LogExchangeSetIsEmpty(SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Begining exchange protocol between {ThisSilo} and {CandidateSilo}.")]
    private partial void LogBeginingProtocol(SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "I just got unblocked from a mutual exchange attempt. I will try the next best candidate (if one is available), otherwise I will wait for my next period to come.")]
    private partial void LogUnblockedMutualExchangeAttempt();

    [LoggerMessage(Level = LogLevel.Debug, Message = "I got an exchange request from {SendingSilo}, but I have been recently involved in another exchange {LastExchangeDuration} ago. My recovery period is {RecoveryPeriod}")]
    private partial void LogExchangedRecently(SiloAddress sendingSilo, TimeSpan lastExchangeDuration, TimeSpan recoveryPeriod);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Exchange request from {ThisSilo} failed, due to {CandidateSilo} having been recently involved in another exchange. I will try the next best candidate (if one is available), otherwise I will wait for my next period to come.")]
    private partial void LogExchangedRecentlyResponse(SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "I got an exchange request from {SendingSilo}, but I am performing one with it at the same time. I have phase-shifted my timer to avoid these conflicts.")]
    private partial void LogMutualExchangeAttempt(SiloAddress sendingSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Exchange request from {ThisSilo} failed, due to an mutual exchange attempt with {CandidateSilo}. I will try the next best candidate (if one is available), otherwise I will wait for my next period to come.")]
    private partial void LogMutualExchangeAttemptResponse(SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "I have successfully finalized my part of the exchange protocol. It was decided that I will take on a total of {ActivationCount} activations.")]
    private partial void LogProtocolFinalized(int activationCount);

    [LoggerMessage(Level = LogLevel.Warning, Message = "En error occured while performing exchange request from {ThisSilo} to {CandidateSilo}. I will try the next best candidate (if one is available), otherwise I will wait for my next period to come. ERROR: {ErrorMessage}")]
    private partial void LogErrorOnProtocolExecution(SiloAddress thisSilo, SiloAddress candidateSilo, string errorMessage);

    [LoggerMessage(Level = LogLevel.Warning, Message = "There was an issue during the migration of the activation set initiated by {ThisSilo}.\n AggregateException: {ErrorMessage}")]
    private partial void LogErrorOnMigratingActivations(SiloAddress thisSilo, string errorMessage);
}