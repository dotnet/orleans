using System;
using FASTER.core;
using Grains.Models;

namespace Grains
{
    public class LookupItemFunctions : IFunctions<int, LookupItem, LookupItem, LookupItem, Empty>
    {
        public void CheckpointCompletionCallback(Guid sessionId, long serialNum)
        {
        }

        public void ConcurrentReader(ref int key, ref LookupItem input, ref LookupItem value, ref LookupItem dst)
        {
            // for concurrent-reader scenario we can return the stored item as-is
            // because it is a single immutable object
            dst = value;
        }

        public void ConcurrentWriter(ref int key, ref LookupItem src, ref LookupItem dst)
        {
            // for concurrent-writer scenario we can replace the stored item as-is
            // because it is a single immutable object
            dst = src;
        }

        public void CopyUpdater(ref int key, ref LookupItem input, ref LookupItem oldValue, ref LookupItem newValue)
        {
            // for a copy update we add a delta and update the timestamp
            newValue = new LookupItem(oldValue.Key, oldValue.Value + newValue.Value, newValue.Timestamp);
        }

        public void DeleteCompletionCallback(ref int key, Empty ctx)
        {
        }

        public void InitialUpdater(ref int key, ref LookupItem input, ref LookupItem value)
        {
            // for the initial update just take the input as-is
            // we can do any initial transformation here if applicable
            value = input;
        }

        public void InPlaceUpdater(ref int key, ref LookupItem input, ref LookupItem value)
        {
            // for an in-place update we add a delta and update the timestamp
            value = new LookupItem(value.Key, value.Value + input.Value, input.Timestamp);
        }

        public void ReadCompletionCallback(ref int key, ref LookupItem input, ref LookupItem output, Empty ctx, Status status)
        {
        }

        public void RMWCompletionCallback(ref int key, ref LookupItem input, Empty ctx, Status status)
        {
        }

        public void SingleReader(ref int key, ref LookupItem input, ref LookupItem value, ref LookupItem dst)
        {
            // for single-reader scenario just return the stored item
            dst = value;
        }

        public void SingleWriter(ref int key, ref LookupItem src, ref LookupItem dst)
        {
            // for single-writer scenario just storage the source item
            dst = src;
        }

        public void UpsertCompletionCallback(ref int key, ref LookupItem value, Empty ctx)
        {
        }
    }
}