﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime;

namespace DependencyInjection.Tests.AspnetDIContainer
{

    [TestCategory("DI"), TestCategory("Functional")]
    public class DependencyInjectionDisambiguationTestsUsingDefaultServiceProvider : DependencyInjectionDisambiguationTestRunner
    {
        protected override IServiceProvider BuildeServiceProvider(IServiceCollection services)
        {
            return services.BuildServiceProvider();
        }
    }
}
