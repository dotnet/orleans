﻿
using System;
using System.Net;
using System.Runtime.Serialization;
using System.Threading.Tasks;
using System.Collections.Generic;
using Orleans.Providers;
using Orleans.Runtime;
using Orleans.Core;
using Orleans.Storage;

namespace Orleans.LogConsistency
{
    /// <summary>
    /// Interface to be implemented for a log consistency provider.
    /// </summary>
    public interface ILogConsistencyProvider : IProvider, ILogViewAdaptorFactory
    {
    }


    /// <summary>
    /// Interface to be implemented for a log-view adaptor factory
    /// </summary>
    public interface ILogViewAdaptorFactory  
    {
        /// <summary> Returns true if a storage provider is required for constructing adaptors. </summary>
        bool UsesStorageProvider { get; }

        /// <summary>
        /// Construct a <see cref="ILogViewAdaptor{TLogView,TLogEntry}"/> to be installed in the given host grain.
        /// </summary>
        ILogViewAdaptor<TLogView, TLogEntry> MakeLogViewAdaptor<TLogView, TLogEntry>(
            ILogViewAdaptorHost<TLogView, TLogEntry> hostgrain,
            TLogView initialstate,
            string graintypename,
            IGrainStorage grainStorage,
            ILogConsistencyProtocolServices services)

            where TLogView : class, new()
            where TLogEntry : class;

    }
}
