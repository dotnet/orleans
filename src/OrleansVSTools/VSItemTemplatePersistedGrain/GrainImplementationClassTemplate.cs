using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using Orleans;
using Orleans.Providers;

namespace $safeprojectname$
{
    /// <summary>
    /// Orleans grain implementation class $safeitemname$
    /// </summary>
    [StorageProvider(ProviderName = "Default")]
    public class $safeitemname$ : Grain<I$safeitemname$State>, I$safeitemname$
	{
    
    }

    public interface I$safeitemname$State
    {

    }
}
