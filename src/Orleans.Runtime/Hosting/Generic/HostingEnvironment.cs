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
    /// Commonly used environment names.
    /// </summary>
    public static class EnvironmentName
    {
        public static readonly string Development = "Development";
        public static readonly string Staging = "Staging";
        public static readonly string Production = "Production";
    }
}
