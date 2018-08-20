using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Tester.AzureUtils.Streaming
{
    public static class AzureQueueUtilities
    {
        public static List<string> GenerateQueueNames(string queueNamePrefix, int queueCount)
        {
            return Enumerable.Range(0, queueCount).Select(num => $"{queueNamePrefix}-{num}").ToList();
        }
    }
}
