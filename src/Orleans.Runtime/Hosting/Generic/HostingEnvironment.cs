// Copyright (c) .NET Foundation. All rights reserved.
// Licensed under the Apache License, Version 2.0. See License.txt in the project root for license information.

namespace Orleans.Hosting
{
    internal class HostingEnvironment : IHostingEnvironment
    {
        public string EnvironmentName { get; set; }

        public string ApplicationName { get; set; }
    }

    /// <summary>
    /// Constants for HostBuilder configuration keys.
    /// </summary>
    public static class HostDefaults
    {
        /// <summary>
        /// The configuration key used to set <see cref="IHostingEnvironment.ApplicationName"/>.
        /// </summary>
        public static readonly string ApplicationKey = "applicationName";

        /// <summary>
        /// The configuration key used to set <see cref="IHostingEnvironment.EnvironmentName"/>.
        /// </summary>
        public static readonly string EnvironmentKey = "environment";

        /// <summary>
        /// The configuration key used to set <see cref="IHostingEnvironment.ContentRootPath"/>
        /// and <see cref="IHostingEnvironment.ContentRootFileProvider"/>.
        /// </summary>
        public static readonly string ContentRootKey = "contentRoot";
    }

    /// <summary>
    /// Commonly used environment names.
    /// </summary>
    public static class EnvironmentName
    {
        public static readonly string Development = "Development";
        public static readonly string Staging = "Staging";
        public static readonly string Production = "Production";
    }
}
