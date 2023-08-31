using System;
using System.Data;
using System.Linq.Expressions;

#if CLUSTERING_ADONET
namespace Orleans.Clustering.AdoNet.Storage
#elif PERSISTENCE_ADONET
namespace Orleans.Persistence.AdoNet.Storage
#elif REMINDERS_ADONET
namespace Orleans.Reminders.AdoNet.Storage
#elif TESTER_SQLUTILS
namespace Orleans.Tests.SqlUtils
#else
// No default namespace intentionally to cause compile errors if something is not defined
#endif
{
    /// <summary>
    /// This interceptor bypasses some Oracle specifics.
    /// </summary>
    internal class OracleCommandInterceptor : ICommandInterceptor
    {
        public static readonly ICommandInterceptor Instance = new OracleCommandInterceptor();

        private readonly Lazy<Action<IDbDataParameter>> setClobOracleDbTypeAction;
        private readonly Lazy<Action<IDbDataParameter>> setBlobOracleDbTypeAction;
        private readonly Lazy<Action<IDbCommand>> setCommandBindByNameAction;


        private OracleCommandInterceptor()
        {
            setClobOracleDbTypeAction = new Lazy<Action<IDbDataParameter>>(() => BuildSetOracleDbTypeAction("Clob"));
            setBlobOracleDbTypeAction = new Lazy<Action<IDbDataParameter>>(() => BuildSetOracleDbTypeAction("Blob"));
            setCommandBindByNameAction = new Lazy<Action<IDbCommand>>(BuildSetBindByNameAction);
        }

        /// <summary>
        /// Creates a compiled lambda which sets the BindByName property on OracleCommand to true.
        /// </summary>
        /// <returns>An action which takes a OracleCommand as IDbCommand </returns>
        private Action<IDbCommand> BuildSetBindByNameAction()
        {
            var type = Type.GetType("Oracle.ManagedDataAccess.Client.OracleCommand, Oracle.ManagedDataAccess");

            var parameterExpression = Expression.Parameter(typeof(IDbCommand), "command");

            var castExpression = Expression.Convert(parameterExpression, type);

            var booleanConstantExpression = Expression.Constant(true);

            var setMethod = type.GetProperty("BindByName").GetSetMethod();

            var callExpression = Expression.Call(castExpression, setMethod, booleanConstantExpression);

            return Expression.Lambda<Action<IDbCommand>>(callExpression, parameterExpression).Compile();
        }

        /// <summary>
        /// Creates a compiled lambda which sets the OracleDbType property to the specified <paramref name="enumName"/>
        /// </summary>
        /// <param name="enumName">String value of a OracleDbType enum value.</param>
        /// <returns>An action which takes a OracleParameter as IDbDataParameter.</returns>
        private Action<IDbDataParameter> BuildSetOracleDbTypeAction(string enumName)
        {
            var type = Type.GetType("Oracle.ManagedDataAccess.Client.OracleParameter, Oracle.ManagedDataAccess");

            var parameterExpression = Expression.Parameter(typeof(IDbDataParameter), "dbparameter");

            var castExpression = Expression.Convert(parameterExpression, type);

            var enumType = Type.GetType("Oracle.ManagedDataAccess.Client.OracleDbType, Oracle.ManagedDataAccess");

            var clob = Enum.Parse(enumType, enumName);

            var enumConstantExpression = Expression.Constant(clob, enumType);

            var setMethod = type.GetProperty("OracleDbType").GetSetMethod();

            var callExpression = Expression.Call(castExpression, setMethod, enumConstantExpression);

            return Expression.Lambda<Action<IDbDataParameter>>(callExpression, parameterExpression).Compile();
        }


        public void Intercept(IDbCommand command)
        {
            foreach (IDbDataParameter commandParameter in command.Parameters)
            {
                //By default oracle binds parameters by index not name
                //The property BindByName must be set to true to change the default behaviour
                setCommandBindByNameAction.Value(command);

                //String parameters are mapped to NVarChar2 OracleDbType which is limited to 4000 bytes
                //This sets the OracleType explicitly to CLOB
                if (commandParameter.ParameterName == "PayloadJson")
                { 
                    setClobOracleDbTypeAction.Value(commandParameter);
                    continue;
                }

                //Same like above
                if (commandParameter.ParameterName == "PayloadXml")
                { 
                    setClobOracleDbTypeAction.Value(commandParameter);
                    continue;
                }

                //Byte arrays are mapped as RAW which causes problems
                //This sets the OracleDbType explicitly to BLOB
                if (commandParameter.ParameterName == "PayloadBinary")
                {
                    setBlobOracleDbTypeAction.Value(commandParameter);
                    continue;
                }

                //Oracle doesn´t support DbType.Boolean, instead
                //we map these to DbType.Int32
                if (commandParameter.DbType == DbType.Boolean)
                {
                    commandParameter.Value = commandParameter.ToString() == bool.TrueString ? 1 : 0;
                    commandParameter.DbType = DbType.Int32;
                }
            }
        }
    }
}
