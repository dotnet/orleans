﻿using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;
using Orleans.Runtime;
using Orleans.Streams;
using UnitTests.GrainInterfaces;

namespace UnitTests.Grains
{
    [ImplicitStreamSubscription(typeof(RedStreamNamespacePredicate))]
    public class FilteredImplicitSubscriptionGrain : Grain, IFilteredImplicitSubscriptionGrain
    {
        private Dictionary<string, int> counters;

        public override async Task OnActivateAsync()
        {
            var logger = this.GetLogger($"{nameof(FilteredImplicitSubscriptionGrain)} {IdentityString}");
            logger.Info("OnActivateAsync");
            var streamProvider = GetStreamProvider("SMSProvider");
            var streamNamespaces = new[] { "red1", "red2", "blue3", "blue4" };
            counters = new Dictionary<string, int>();
            foreach (var streamNamespace in streamNamespaces)
            {
                counters[streamNamespace] = 0;
                var stream = streamProvider.GetStream<int>(this.GetPrimaryKey(), streamNamespace);
                await stream.SubscribeAsync(
                    (e, t) =>
                    {
                        logger.Info($"Received a {streamNamespace} event {e}");
                        counters[streamNamespace]++;
                        return Task.CompletedTask;
                    });
            }
        }

        public Task<int> GetCounter(string streamNamespace)
        {
            return Task.FromResult(counters[streamNamespace]);
        }
    }
}