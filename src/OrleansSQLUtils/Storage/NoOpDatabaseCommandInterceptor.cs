using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace OrleansSQLUtils.Storage
{
    internal class NoOpDatabaseCommandInterceptor : IDatabaseCommandInterceptor
    {
        public static readonly IDatabaseCommandInterceptor Instance = new NoOpDatabaseCommandInterceptor();

        private NoOpDatabaseCommandInterceptor()
        {
            
        }

        public void Intercept(IDbCommand command)
        {
            //NOP
        }
    }
}
