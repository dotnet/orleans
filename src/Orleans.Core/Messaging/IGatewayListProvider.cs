using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Messaging
{
    /// <summary>
    /// Interface that provides Orleans gateways information.
    /// </summary>
    public interface IGatewayListProvider
    {
        /// <summary>
        /// Initializes the provider, will be called before all other methods
        /// </summary>
        Task InitializeGatewayListProvider();
        /// <summary>
        /// Returns the list of gateways (silos) that can be used by a client to connect to Orleans cluster.
        /// The Uri is in the form of: "gwy.tcp://IP:port/Generation". See Utils.ToGatewayUri and Utils.ToSiloAddress for more details about Uri format.
        /// </summary>
        Task<IList<Uri>> GetGateways();

        /// <summary>
        /// Specifies how often this IGatewayListProvider is refreshed, to have a bound on max staleness of its returned information.
        /// </summary>
        TimeSpan MaxStaleness { get; }

        /// <summary>
        /// Specifies whether this IGatewayListProvider ever refreshes its returned information, or always returns the same gateway list.
        /// </summary>
        [Obsolete("This attribute is no longer used and all providers are considered updatable")]
        bool IsUpdatable { get; }
    }

    /// <summary>
    /// A listener interface for optional GatewayList notifications provided by the IGatewayListObservable interface.
    /// </summary>
    public interface IGatewayListListener
    {
        void GatewayListNotification(IEnumerable<Uri> gateways);
    }

    /// <summary>
    /// An optional interface that GatewayListProvider may implement if it support out of band gw update notifications.
    /// By default GatewayListProvider should support pull based queries (GetGateways).
    /// Optionally, some GatewayListProviders may be able to notify a listener if an updated gw information is available.
    /// This is optional and not required.
    /// </summary>
    public interface IGatewayListObservable
    {
        bool SubscribeToGatewayNotificationEvents(IGatewayListListener listener);

        bool UnSubscribeFromGatewayNotificationEvents(IGatewayListListener listener);
    }
}
