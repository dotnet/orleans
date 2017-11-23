using System;
using System.Collections.Generic;
using System.Data;
using System.Text;

namespace OrleansSQLUtils.Storage
{
    internal interface IDatabaseCommandInterceptor
    {
        void Intercept(IDbCommand command);
    }
}
