using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Runtime.MembershipService;
using Orleans.Runtime.ReminderService;

namespace Orleans.Runtime.Startup
{
    public interface IStartup
    {
        IServiceProvider ConfigureServices(IServiceCollection services);
    }
}
