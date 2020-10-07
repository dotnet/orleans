using System;
using System.Collections.Generic;
using System.Text;
using Orleans.Runtime;

namespace Orleans
{
    internal interface IGrainIdLoggingHelper
    {
        string GetGrainTypeName(int typeCode);

        string GetSystemTargetName(GrainId grainId);
    }
}
