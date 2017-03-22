using Orleans.Runtime;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Streams.Providers
{
    internal static class StreamProviderUtils
    {
        public static async Task<StreamConsumerExtension> BindExtensionLazy(IStreamProviderRuntime providerRuntime, Logger logger, bool IsRewindable, AsyncLock bindExtLock)
        {
            using (await bindExtLock.LockAsync())
            {
                if (logger.IsVerbose) logger.Verbose("BindExtensionLazy - Binding local extension to stream runtime={0}", providerRuntime);
                var tup = await providerRuntime.BindExtension<StreamConsumerExtension, IStreamConsumerExtension>(
                    () => new StreamConsumerExtension(providerRuntime, IsRewindable));
                var myExtension = tup.Item1;
                var myGrainReference = tup.Item2;
                if (logger.IsVerbose) logger.Verbose("BindExtensionLazy - Connected Extension={0} GrainRef={1}", myExtension, myGrainReference);
                return myExtension;
            }
        }
    }
}
