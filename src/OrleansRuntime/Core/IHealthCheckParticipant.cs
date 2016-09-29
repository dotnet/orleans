using System;


namespace Orleans.Runtime
{
    internal interface IHealthCheckParticipant
    {
        bool CheckHealth(DateTime lastCheckTime);
    }
}

