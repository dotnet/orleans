using System;
using System.Collections.Generic;
using System.Text;

namespace Orleans.Transactions.Abstractions
{
    public interface ITransactionCommitterFactory
    {
        ITransactionCommitter<TService> Create<TService>(ITransactionCommitterConfiguration config) where TService : class;
    }
}
