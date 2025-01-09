using System;
using Microsoft.Extensions.Logging;

namespace Orleans.Runtime.Placement.Repartitioning;

internal partial class ActivationRepartitioner
{
    [LoggerMessage(Level = LogLevel.Debug, Message = "I will periodically initiate the exchange protocol every {MinPeriod} to {MaxPeriod} starting in {DueTime}.")]
    private partial void LogPeriodicallyInvokeProtocol(TimeSpan minPeriod, TimeSpan maxPeriod, TimeSpan dueTime);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Activation repartitioning is enabled, but the cluster contains only one silo. Waiting for at least another silo to join the cluster to proceed.")]
    private partial void LogSingleSiloCluster();

    [LoggerMessage(Level = LogLevel.Debug, Message = "Exchange set for candidate silo {CandidateSilo} is empty. I will try the next best candidate (if one is available), otherwise I will wait for my next period to come.")]
    private partial void LogExchangeSetIsEmpty(SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Beginning exchange protocol between {ThisSilo} and {CandidateSilo}.")]
    private partial void LogBeginningProtocol(SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "I got an exchange request from {SendingSilo}, but I have been recently involved in another exchange {LastExchangeDuration} ago. My recovery period is {RecoveryPeriod}")]
    private partial void LogExchangedRecently(SiloAddress sendingSilo, TimeSpan lastExchangeDuration, TimeSpan recoveryPeriod);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Exchange request from {ThisSilo} rejected: {CandidateSilo} was recently involved in another exchange. Attempting the next best candidate (if one is available) or waiting for my next period to come.")]
    private partial void LogExchangedRecentlyResponse(SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Rejecting exchange request from {SendingSilo} since we are already exchanging with that host.")]
    private partial void LogMutualExchangeAttempt(SiloAddress sendingSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Exchange request from {ThisSilo} superseded by a mutual exchange attempt with {CandidateSilo}.")]
    private partial void LogMutualExchangeAttemptResponse(SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Finalized exchange protocol: migrating {GivingActivationCount} activations, receiving {TakingActivationCount} activations.")]
    private partial void LogProtocolFinalized(int givingActivationCount, int takingActivationCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Finalized exchange protocol: migrating [{GivingActivations}], receiving [{TakingActivations}].")]
    private partial void LogProtocolFinalizedTrace(string givingActivations, string takingActivations);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error performing exchange request from {ThisSilo} to {CandidateSilo}. I will try the next best candidate (if one is available), otherwise I will wait for my next period to come.")]
    private partial void LogErrorOnProtocolExecution(Exception exception, SiloAddress thisSilo, SiloAddress candidateSilo);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error migrating exchange set.")]
    private partial void LogErrorOnMigratingActivations(Exception exception);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Received AcceptExchangeRequest from {SendingSilo}, offering to send {ExchangeSetCount} activations from a total of {ActivationCount} activations.")]
    private partial void LogReceivedExchangeRequest(SiloAddress sendingSilo, int exchangeSetCount, int activationCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Imbalance is {Imbalance} (remote: {RemoteCount} vs local {LocalCount})")]
    private partial void LogImbalance(int imbalance, int remoteCount, int localCount);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Computing transfer set took {Elapsed}. Anticipated imbalance after transfer is {AnticipatedImbalance}.")]
    private partial void LogTransferSetComputed(TimeSpan elapsed, int anticipatedImbalance);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Error accepting exchange request from {SendingSilo}.")]
    private partial void LogErrorAcceptingExchangeRequest(Exception exception, SiloAddress sendingSilo);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Waiting an additional {CoolDown} to cool down before initiating the exchange protocol.")]
    private partial void LogCoolingDown(TimeSpan coolDown);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Adding {NewlyAnchoredGrains} newly anchored grains to set on host {Silo}. EdgeWeights contains {EdgeWeightCount} elements.")]
    private partial void LogAddingAnchoredGrains(int newlyAnchoredGrains, SiloAddress silo, int edgeWeightCount);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Candidate sets computed in {Elapsed}.")]
    private partial void LogComputedCandidateSets(TimeSpan elapsed);

    [LoggerMessage(Level = LogLevel.Trace, Message = "Candidate heaps created in {Elapsed}.")]
    private partial void LogComputedCandidateHeaps(TimeSpan elapsed);
}
