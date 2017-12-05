using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace OrleansSQLUtils.Storage
{
    internal interface ICommandInterceptor
    {
        void Intercept(IDbCommand command);
    }
}
