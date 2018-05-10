﻿using Orleans.Transactions;
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;

namespace Orleans.Transactions
{
    internal class DisabledTransactionAgent : ITransactionAgent
    {
        public void Abort(ITransactionInfo transactionInfo, OrleansTransactionAbortedException reason)
        {
            throw new OrleansTransactionsDisabledException();
        }

        public Task Commit(ITransactionInfo transactionInfo)
        {
            throw new OrleansTransactionsDisabledException();
        }

        public Task Start()
        {
            return Task.CompletedTask;
        }

        public Task<ITransactionInfo> StartTransaction(bool readOnly, TimeSpan timeout)
        {
            throw new OrleansStartTransactionFailedException(new OrleansTransactionsDisabledException());
        }
    }
}
