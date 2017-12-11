using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace OrleansSQLUtils.Storage
{
    internal class NoOpCommandInterceptor : ICommandInterceptor
    {
        public static readonly ICommandInterceptor Instance = new NoOpCommandInterceptor();

        private NoOpCommandInterceptor()
        {
            
        }

        public void Intercept(IDbCommand command)
        {
            //NOP
        }
    }
}
