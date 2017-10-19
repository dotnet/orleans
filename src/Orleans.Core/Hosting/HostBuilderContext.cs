// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

using System.Collections.Generic;
using Microsoft.Extensions.Configuration;

namespace Orleans.Hosting
{
    /// <summary>
    /// Context containing the common services on the host. Some properties may be null until set by the host.
    /// </summary>
    public class HostBuilderContext
    {
        public HostBuilderContext(IDictionary<object, object> properties)
        {
            Properties = properties ?? throw new System.ArgumentNullException(nameof(properties));
        }

        /// <summary>
        /// The <see cref="IConfiguration" /> containing the merged configuration of the application and the host.
        /// </summary>
        public IConfiguration Configuration { get; set; }
        
        /// <summary>
        /// A central location for sharing state between components during the host building process.
        /// </summary>
        public IDictionary<object, object> Properties { get; }
    }
}
