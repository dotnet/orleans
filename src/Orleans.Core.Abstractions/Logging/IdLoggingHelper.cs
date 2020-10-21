using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Runtime;

namespace Orleans
{
    internal interface IGrainIdLoggingHelper
    {
        string GetGrainTypeName(GrainType grainType);
    }

    internal interface IInvokeMethodRequestLoggingHelper
    {
        void GetInterfaceAndMethodName(int interfaceTypeCode, int methodId, out string interfaceName, out string methodName);
    }
}
