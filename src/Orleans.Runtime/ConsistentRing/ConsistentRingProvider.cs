using System;
using System.Collections.Generic;
using System.Text;
using Microsoft.Extensions.Logging;


namespace Orleans.Runtime.ConsistentRing
{
    /// <summary>
    /// We use the 'backward/clockwise' definition to assign responsibilities on the ring. 
    /// E.g. in a ring of nodes {5, 10, 15} the responsible for key 7 is 10 (the node is responsible for its preceding range). 
    /// The backwards/clockwise approach is consistent with many overlays, e.g., Chord, Cassandra, etc.
    /// Note: MembershipOracle uses 'forward/counter-clockwise' definition to assign responsibilities. 
    /// E.g. in a ring of nodes {5, 10, 15}, the responsible of key 7 is node 5 (the node is responsible for its succeeding range).
    /// </summary>
    internal sealed partial class ConsistentRingProvider :
        IConsistentRingProvider, ISiloStatusListener, IDisposable
    {
        // internal, so that unit tests can access them
        internal SiloAddress MyAddress { get; }
        private IRingRange myRange;

        /// list of silo members sorted by the hash value of their address
        private readonly List<SiloAddress> membershipRingList = new();
        private readonly ILogger log;
        private bool isRunning;
        private readonly int myKey;
        private readonly List<IRingRangeListener> statusListeners = new();
        private readonly ISiloStatusOracle _siloStatusOracle;
        private (IRingRange OldRange, IRingRange NewRange, bool Increased) lastNotification;

        public ConsistentRingProvider(SiloAddress siloAddr, ILoggerFactory loggerFactory, ISiloStatusOracle siloStatusOracle)
        {
            log = loggerFactory.CreateLogger<ConsistentRingProvider>();
            MyAddress = siloAddr;
            _siloStatusOracle = siloStatusOracle;
            myKey = MyAddress.GetConsistentHashCode();

            myRange = RangeFactory.CreateFullRange(); // i am responsible for the whole range
            lastNotification = (myRange, myRange, true);

            // add myself to the list of members
            AddServer(MyAddress);
            Start();
            siloStatusOracle.SubscribeToSiloStatusEvents(this);
        }

        /// <summary>
        /// Returns the silo that this silo thinks is the primary owner of the key
        /// </summary>
        /// <param name="key"></param>
        /// <returns></returns>
        public SiloAddress GetPrimaryTargetSilo(uint key)
        {
            return CalculateTargetSilo(key);
        }

        public IRingRange GetMyRange()
        {
            return myRange; // its immutable, so no need to clone
        }

        private void Start()
        {
            isRunning = true;
        }

        private void Stop()
        {
            isRunning = false;
        }

        internal void AddServer(SiloAddress silo)
        {
            lock (membershipRingList)
            {
                if (membershipRingList.Contains(silo)) return; // we already have this silo

                int myOldIndex = membershipRingList.IndexOf(MyAddress);

                if (!(membershipRingList.Count == 0 || myOldIndex != -1))
                    throw new OrleansException(string.Format("{0}: Couldn't find my position in the ring {1}.", MyAddress, Utils.EnumerableToString(membershipRingList)));

                // insert new silo in the sorted order
                int hash = silo.GetConsistentHashCode();

                // Find the last silo with hash smaller than the new silo, and insert the latter after (this is why we have +1 here) the former.
                // Notice that FindLastIndex might return -1 if this should be the first silo in the list, but then
                // 'index' will get 0, as needed.
                int index = membershipRingList.FindLastIndex(siloAddr => siloAddr.GetConsistentHashCode() < hash) + 1;
                membershipRingList.Insert(index, silo);

                // relating to triggering handler ... new node took over some of my responsibility
                if (index == myOldIndex || // new node was inserted in my place
                    (myOldIndex == 0 && index == membershipRingList.Count - 1)) // I am the first node, and the new server is the last node
                {
                    IRingRange oldRange = myRange;
                    myRange = RangeFactory.CreateRange(unchecked((uint)hash), unchecked((uint)myKey));
                    NotifyLocalRangeSubscribers(oldRange, myRange, false);
                }

                LogDebugAddedServer(log, new(silo), this);
            }
        }

        public override string ToString()
        {
            lock (membershipRingList)
            {
                if (membershipRingList.Count == 1)
                    return $"[{membershipRingList[0]:H} -> {RangeFactory.CreateFullRange()}]";

                var sb = new StringBuilder().Append('[');
                for (int i = 0; i < membershipRingList.Count; i++)
                {
                    SiloAddress curr = membershipRingList[i];
                    SiloAddress next = membershipRingList[(i + 1) % membershipRingList.Count];
                    IRingRange range = RangeFactory.CreateRange(unchecked((uint)curr.GetConsistentHashCode()), unchecked((uint)next.GetConsistentHashCode()));
                    sb.Append($"{curr:H} -> {range},  ");
                }

                return sb.Append(']').ToString();
            }
        }

        internal void RemoveServer(SiloAddress silo)
        {
            lock (membershipRingList)
            {
                int indexOfFailedSilo = membershipRingList.IndexOf(silo);
                if (indexOfFailedSilo < 0) return; // we have already removed this silo

                membershipRingList.RemoveAt(indexOfFailedSilo);

                // related to triggering handler
                int myNewIndex = membershipRingList.IndexOf(MyAddress);

                if (myNewIndex == -1)
                    throw new OrleansException($"{MyAddress}: Couldn't find my position in the ring {this}.");

                bool wasMyPred = ((myNewIndex == indexOfFailedSilo) || (myNewIndex == 0 && indexOfFailedSilo == membershipRingList.Count)); // no need for '- 1'
                if (wasMyPred) // failed node was our predecessor
                {
                    LogDebugFailedServerWasMyPredecessor(log, wasMyPred, this);

                    IRingRange oldRange = myRange;
                    if (membershipRingList.Count == 1) // i'm the only one left
                    {
                        myRange = RangeFactory.CreateFullRange();
                        NotifyLocalRangeSubscribers(oldRange, myRange, true);
                    }
                    else
                    {
                        int myNewPredIndex = myNewIndex == 0 ? membershipRingList.Count - 1 : myNewIndex - 1;
                        int myPredecessorsHash = membershipRingList[myNewPredIndex].GetConsistentHashCode();

                        myRange = RangeFactory.CreateRange(unchecked((uint)myPredecessorsHash), unchecked((uint)myKey));
                        NotifyLocalRangeSubscribers(oldRange, myRange, true);
                    }
                }

                LogDebugRemovedServer(log, silo, new(silo), this);
            }
        }

        public bool SubscribeToRangeChangeEvents(IRingRangeListener observer)
        {
            (IRingRange OldRange, IRingRange NewRange, bool Increased) notification;
            lock (statusListeners)
            {
                if (statusListeners.Contains(observer)) return false;

                statusListeners.Add(observer);
                notification = lastNotification;
            }

            observer.RangeChangeNotification(notification.OldRange, notification.NewRange, notification.Increased);
            return true;
        }

        public bool UnSubscribeFromRangeChangeEvents(IRingRangeListener observer)
        {
            lock (statusListeners)
            {
                return statusListeners.Remove(observer);
            }
        }

        private void NotifyLocalRangeSubscribers(IRingRange old, IRingRange now, bool increased)
        {
            LogDebugNotifyLocalRangeSubscribers(log, old, now, increased);

            IRingRangeListener[] copy;
            lock (statusListeners)
            {
                lastNotification = (old, now, increased);
                copy = statusListeners.ToArray();
            }

            foreach (IRingRangeListener listener in copy)
            {
                try
                {
                    listener.RangeChangeNotification(old, now, increased);
                }
                catch (Exception exc)
                {
                    LogWarningErrorNotifyingListener(log, exc, listener.GetType().FullName, increased ? "expansion" : "contraction", old, now);
                }
            }
        }

        public void SiloStatusChangeNotification(SiloAddress updatedSilo, SiloStatus status)
        {
            // This silo's status has changed
            if (updatedSilo.Equals(MyAddress))
            {
                if (status.IsTerminating())
                {
                    Stop();
                }
            }
            else // Status change for some other silo
            {
                if (status.IsTerminating())
                {
                    RemoveServer(updatedSilo);
                }
                else if (status == SiloStatus.Active)      // do not do anything with SiloStatus.Created or SiloStatus.Joining -- wait until it actually becomes active
                {
                    AddServer(updatedSilo);
                }
            }
        }

        /// <summary>
        /// Finds the silo that owns the given hash value.
        /// This routine will always return a non-null silo address unless the excludeThisSiloIfStopping parameter is true,
        /// this is the only silo known, and this silo is stopping.
        /// </summary>
        /// <param name="hash"></param>
        /// <param name="excludeThisSiloIfStopping"></param>
        /// <returns></returns>
        private SiloAddress CalculateTargetSilo(uint hash, bool excludeThisSiloIfStopping = true)
        {
            SiloAddress siloAddress = null;

            lock (membershipRingList)
            {
                // excludeMySelf from being a TargetSilo if we're not running and the excludeThisSIloIfStopping flag is true. see the comment in the Stop method.
                bool excludeMySelf = excludeThisSiloIfStopping && !isRunning;

                if (membershipRingList.Count == 0)
                {
                    // If the membership ring is empty, then we're the owner by default unless we're stopping.
                    return excludeMySelf ? null : MyAddress;
                }

                // use clockwise ... current code in membershipOracle.CalculateTargetSilo() does counter-clockwise ...
                // if you want to stick to counter-clockwise, change the responsibility definition in 'In()' method & responsibility defs in OrleansReminderMemory
                // need to implement a binary search, but for now simply traverse the list of silos sorted by their hashes

                for (int index = 0; index < membershipRingList.Count; ++index)
                {
                    var siloAddr = membershipRingList[index];
                    if (IsSiloNextInTheRing(siloAddr, hash, excludeMySelf))
                    {
                        siloAddress = siloAddr;
                        break;
                    }
                }

                if (siloAddress == null)
                {
                    // if not found in traversal, then first silo should be returned (we are on a ring)
                    // if you go back to their counter-clockwise policy, then change the 'In()' method in OrleansReminderMemory
                    siloAddress = membershipRingList[0]; // vs [membershipRingList.Count - 1]; for counter-clockwise policy
                    // Make sure it's not us...
                    if (siloAddress.Equals(MyAddress) && excludeMySelf)
                    {
                        // vs [membershipRingList.Count - 2]; for counter-clockwise policy
                        siloAddress = membershipRingList.Count > 1 ? membershipRingList[1] : null;
                    }
                }
            }

            LogTraceCalculatedRingPartitionOwner(log, MyAddress, siloAddress, hash, new(siloAddress));
            return siloAddress;
        }

        private bool IsSiloNextInTheRing(SiloAddress siloAddr, uint hash, bool excludeMySelf)
        {
            return siloAddr.GetConsistentHashCode() >= hash && (!siloAddr.Equals(MyAddress) || !excludeMySelf);
        }

        public void Dispose()
        {
            _siloStatusOracle.UnSubscribeFromSiloStatusEvents(this);
        }

        private readonly struct SiloAddressWithHashLogRecord(SiloAddress siloAddress)
        {
            public override string ToString() => siloAddress.ToStringWithHashCode();
        }

        private readonly struct ConsistentHashCodeLogRecord(SiloAddress siloAddress)
        {
            public override string ToString() => siloAddress?.GetConsistentHashCode().ToString();
        }

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Added Server {SiloAddress}. Current view: {CurrentView}"
        )]
        private static partial void LogDebugAddedServer(ILogger logger, SiloAddressWithHashLogRecord siloAddress, ConsistentRingProvider currentView);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Failed server was my predecessor? {WasPredecessor}, updated view {CurrentView}"
        )]
        private static partial void LogDebugFailedServerWasMyPredecessor(ILogger logger, bool wasPredecessor, ConsistentRingProvider currentView);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "Removed Server {SiloAddress} hash {Hash}. Current view {CurrentView}"
        )]
        private static partial void LogDebugRemovedServer(ILogger logger, SiloAddress siloAddress, SiloAddressWithHashLogRecord hash, ConsistentRingProvider currentView);

        [LoggerMessage(
            Level = LogLevel.Debug,
            Message = "NotifyLocalRangeSubscribers about old {OldRange} new {NewRange} increased? {IsIncreased}"
        )]
        private static partial void LogDebugNotifyLocalRangeSubscribers(ILogger logger, IRingRange oldRange, IRingRange newRange, bool isIncreased);

        [LoggerMessage(
            EventId = (int)ErrorCode.CRP_Local_Subscriber_Exception,
            Level = LogLevel.Warning,
            Message = "Error notifying listener '{ListenerType}' of ring range {AdjustmentKind} from '{OldRange}' to '{NewRange}'."
        )]
        private static partial void LogWarningErrorNotifyingListener(ILogger logger, Exception exception, string listenerType, string adjustmentKind, IRingRange oldRange, IRingRange newRange);

        [LoggerMessage(
            Level = LogLevel.Trace,
            Message = "Silo {SiloAddress} calculated ring partition owner silo {OwnerAddress} for key {Key}: {Key} --> {OwnerHash}"
        )]
        private static partial void LogTraceCalculatedRingPartitionOwner(ILogger logger, SiloAddress siloAddress, SiloAddress ownerAddress, uint key, ConsistentHashCodeLogRecord ownerHash);
    }
}
