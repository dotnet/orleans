using System;

namespace Orleans.Transactions
{
    public class Clock : IClock
    {
        public DateTime UtcNow() => DateTime.UtcNow;
    }
}
