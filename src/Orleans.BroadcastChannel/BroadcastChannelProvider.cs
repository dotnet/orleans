using System;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Orleans.BroadcastChannel.SubscriberTable;
using Orleans.Providers;
using Orleans.Runtime;

namespace Orleans.BroadcastChannel
{
    public interface IBroadcastChannelProvider
    {
        IBroadcastChannelWriter<T> GetChannelWriter<T>(ChannelId streamId);
    }

    internal class BroadcastChannelProvider : IBroadcastChannelProvider
    {
        private readonly string _providerName;
        private readonly BroadcastChannelOptions _options;
        private readonly IGrainFactory _grainFactory;
        private readonly ImplicitChannelSubscriberTable _subscriberTable;
        private readonly ILoggerFactory _loggerFactory;

        public BroadcastChannelProvider(
            string providerName,
            BroadcastChannelOptions options,
            IGrainFactory grainFactory,
            ImplicitChannelSubscriberTable subscriberTable,
            ILoggerFactory loggerFactory)
        {
            _providerName = providerName;
            _options = options;
            _grainFactory = grainFactory;
            _subscriberTable = subscriberTable;
            _loggerFactory = loggerFactory;
        }

        public IBroadcastChannelWriter<T> GetChannelWriter<T>(ChannelId streamId)
        {
            return new BroadcastChannelWriter<T>(
                new InternalChannelId(_providerName, streamId),
                _grainFactory,
                _subscriberTable,
                _options.FireAndForgetDelivery,
                _loggerFactory);
        }

        public static IBroadcastChannelProvider Create(IServiceProvider sp, string name)
        {
            var opt = sp.GetOptionsByName<BroadcastChannelOptions>(name);
            return ActivatorUtilities.CreateInstance<BroadcastChannelProvider>(sp, name, sp.GetOptionsByName<BroadcastChannelOptions>(name));
        }
    }
}

