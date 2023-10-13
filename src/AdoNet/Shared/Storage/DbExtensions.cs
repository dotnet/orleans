using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;
using System.Data.Common;
using System.Threading;
using System.Threading.Tasks;

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
    /// Contains some convenience methods to use in conjunction with <see cref="IRelationalStorage">IRelationalStorage</see> and <see cref="RelationalStorage">GenericRelationalStorage</see>.
    /// </summary>
    internal static class DbExtensions
    {
        /// <summary>
        /// An explicit map of type CLR viz database type conversions.
        /// </summary>
        private static readonly ReadOnlyDictionary<Type, DbType> typeMap = new ReadOnlyDictionary<Type, DbType>(new Dictionary<Type, DbType>
        {
            { typeof(object),   DbType.Object },
            { typeof(int),      DbType.Int32 },
            { typeof(int?),     DbType.Int32 },
            { typeof(uint),     DbType.UInt32 },
            { typeof(uint?),    DbType.UInt32 },
            { typeof(long),     DbType.Int64 },
            { typeof(long?),    DbType.Int64 },
            { typeof(ulong),    DbType.UInt64 },
            { typeof(ulong?),   DbType.UInt64 },
            { typeof(float),    DbType.Single },
            { typeof(float?),   DbType.Single },
            { typeof(double),   DbType.Double },
            { typeof(double?),  DbType.Double },
            { typeof(decimal),  DbType.Decimal },
            { typeof(decimal?), DbType.Decimal },
            { typeof(short),    DbType.Int16 },
            { typeof(short?),   DbType.Int16 },
            { typeof(ushort),   DbType.UInt16 },
            { typeof(ushort?),  DbType.UInt16 },
            { typeof(byte),     DbType.Byte },
            { typeof(byte?),    DbType.Byte },
            { typeof(sbyte),    DbType.SByte },
            { typeof(sbyte?),   DbType.SByte },
            { typeof(bool),     DbType.Boolean },
            { typeof(bool?),    DbType.Boolean },
            { typeof(string),   DbType.String },
            { typeof(char),     DbType.StringFixedLength },
            { typeof(char?),    DbType.StringFixedLength },
            { typeof(Guid),     DbType.Guid },
            { typeof(Guid?),    DbType.Guid },
            //Using DateTime for cross DB compatibility. The underlying DB table column type can be DateTime or DateTime2
            { typeof(DateTime),     DbType.DateTime },
            { typeof(DateTime?),    DbType.DateTime },
            { typeof(TimeSpan),     DbType.Time },
            { typeof(byte[]),       DbType.Binary },
            { typeof(TimeSpan?),        DbType.Time },
            { typeof(DateTimeOffset),   DbType.DateTimeOffset },
            { typeof(DateTimeOffset?),  DbType.DateTimeOffset },
        });

        /// <summary>
        /// Creates a new SQL parameter using the given arguments.
        /// </summary>
        /// <typeparam name="T">The type of the parameter.</typeparam>
        /// <param name="command">The command to use to create the parameter.</param>
        /// <param name="direction">The direction of the parameter.</param>
        /// <param name="parameterName">The name of the parameter.</param>
        /// <param name="value">The value of the parameter.</param>
        /// <param name="size">The size of the parameter value.</param>
        /// <param name="dbType">the <see cref="DbType"/> of the parameter.</param>
        /// <returns>A parameter created using the given arguments.</returns>
        public static IDbDataParameter CreateParameter<T>(this IDbCommand command, ParameterDirection direction, string parameterName, T value, int? size = null, DbType? dbType = null)
        {
            //There should be no boxing for value types. See at:
            //http://stackoverflow.com/questions/8823239/comparing-a-generic-against-null-that-could-be-a-value-or-reference-type
            var parameter = command.CreateParameter();
            parameter.ParameterName = parameterName;
            parameter.Value = (object)value ?? DBNull.Value;
            parameter.DbType = dbType ?? typeMap[typeof(T)];
            parameter.Direction = direction;
            if (size != null) { parameter.Size = size.Value; }

            return parameter;
        }

        /// <summary>
        /// Creates and adds a new SQL parameter to the command.
        /// </summary>
        /// <typeparam name="T">The type of the parameter.</typeparam>
        /// <param name="command">The command to use to create the parameter.</param>
        /// <param name="parameterName">The name of the parameter.</param>
        /// <param name="value">The value of the parameter.</param>
        /// <param name="direction">The direction of the parameter.</param>
        /// <param name="size">The size of the parameter value.</param>
        /// <param name="dbType">the <see cref="DbType"/> of the parameter.</param>
        public static void AddParameter<T>(this IDbCommand command, string parameterName, T value, ParameterDirection direction = ParameterDirection.Input, int? size = null, DbType? dbType = null)
        {
            command.Parameters.Add(command.CreateParameter(direction, parameterName, value, size));
        }

        /// <summary>
        /// Returns a value if it is not <see cref="System.DBNull"/>, <em>default(TValue)</em> otherwise.
        /// </summary>
        /// <typeparam name="TValue">The type of the value to request.</typeparam>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field to retrieve.</param>
        /// <param name="default">The default value if value in position is <see cref="System.DBNull"/>.</param>
        /// <returns>Either the given value or the default for the requested type.</returns>
        /// <remarks>This function throws if the given <see paramref="fieldName"/> does not exist.</remarks>
        public static TValue GetValueOrDefault<TValue>(this IDataRecord record, string fieldName, TValue @default = default)
        {

            try
            {
                var ordinal = record.GetOrdinal(fieldName);
                return record.IsDBNull(ordinal) ? @default : (TValue)record.GetValue(ordinal);
            }
            catch (IndexOutOfRangeException e)
            {
                throw new DataException($"Field '{fieldName}' not found in data record.", e);
            }
        }
        /// <summary>
        /// Returns a value if it is not <see cref="System.DBNull"/>, <em>default</em>  otherwise.
        /// </summary>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field to retrieve.</param>
        /// <param name="default">The default value if value in position is <see cref="System.DBNull"/>.</param>
        /// <returns>Either the given value or the default for <see cref="System.DateTime"/>?.</returns>
        /// <exception cref="DataException"/>
        /// <remarks>An explicit function like this is needed in cases where to connector infers a type that is undesirable.
        /// An example here is Npgsql.NodaTime, which makes Npgsql return Noda type and consequently Orleans is not able to
        /// use it since it expects .NET <see cref="System.DateTime"/>. This function throws if the given <see paramref="fieldName"/> does not exist.</remarks>
        public static DateTime? GetDateTimeValueOrDefault(this IDataRecord record, string fieldName, DateTime? @default = default)
        {

            try
            {
                var ordinal = record.GetOrdinal(fieldName);
                return record.IsDBNull(ordinal) ? @default : record.GetDateTime(ordinal);
            }
            catch (IndexOutOfRangeException e)
            {
                throw new DataException($"Field '{fieldName}' not found in data record.", e);
            }
        }

        /// <summary>
        /// Returns a value if it is not <see cref="System.DBNull"/>, <em>default(TValue)</em> otherwise.
        /// </summary>
        /// <typeparam name="TValue">The type of the value to request.</typeparam>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field to retrieve.</param>
        /// <param name="default">The default value if value in position is <see cref="System.DBNull"/>.</param>
        /// <returns>Either the given value or the default for the requested type.</returns>
        /// <exception cref="DataException"/>
        /// <remarks>This function throws if the given <see paramref="fieldName"/> does not exist.</remarks>
        public static async Task<TValue> GetValueOrDefaultAsync<TValue>(this DbDataReader record, string fieldName, TValue @default = default)
        {
            try
            {
                var ordinal = record.GetOrdinal(fieldName);
                return (await record.IsDBNullAsync(ordinal).ConfigureAwait(false))
                    ? @default
                    : (await record.GetFieldValueAsync<TValue>(ordinal).ConfigureAwait(false));
            }
            catch (IndexOutOfRangeException e)
            {
                throw new DataException($"Field '{fieldName}' not found in data record.", e);
            }
        }


        /// <summary>
        /// Returns a value if it is not <see cref="System.DBNull"/>, <em>default(TValue)</em> otherwise.
        /// </summary>
        /// <typeparam name="TValue">The type of the value to request.</typeparam>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="ordinal">The ordinal of the fieldname.</param>
        /// <param name="default">The default value if value in position is <see cref="System.DBNull"/>.</param>
        /// <returns>Either the given value or the default for the requested type.</returns>
        /// <exception cref="IndexOutOfRangeException"/>                
        public static TValue GetValueOrDefault<TValue>(this IDataRecord record, int ordinal, TValue @default = default)
        {
            return record.IsDBNull(ordinal) ? @default : (TValue)record.GetValue(ordinal);
        }


        /// <summary>
        /// Returns a value if it is not <see cref="System.DBNull"/>, <em>default(TValue)</em> otherwise.
        /// </summary>
        /// <typeparam name="TValue">The type of the value to request.</typeparam>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="ordinal">The ordinal of the fieldname.</param>
        /// <param name="default">The default value if value in position is <see cref="System.DBNull"/>.</param>
        /// <returns>Either the given value or the default for the requested type.</returns>
        /// <exception cref="IndexOutOfRangeException"/>                
        public static async Task<TValue> GetValueOrDefaultAsync<TValue>(this DbDataReader record, int ordinal, TValue @default = default)
        {

            return (await record.IsDBNullAsync(ordinal).ConfigureAwait(false)) ? @default : (await record.GetFieldValueAsync<TValue>(ordinal).ConfigureAwait(false));
        }


        /// <summary>
        /// Returns a value with the given <see paramref="fieldName"/>.
        /// </summary>
        /// <typeparam name="TValue">The type of value to retrieve.</typeparam>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field.</param>
        /// <returns>Value in the given field indicated by <see paramref="fieldName"/>.</returns>
        /// <exception cref="DataException"/>
        /// <remarks>This function throws if the given <see paramref="fieldName"/> does not exist.</remarks>        
        public static TValue GetValue<TValue>(this IDataRecord record, string fieldName)
        {
            try
            {
                var ordinal = record.GetOrdinal(fieldName);
                return (TValue)record.GetValue(ordinal);
            }
            catch (IndexOutOfRangeException e)
            {
                throw new DataException($"Field '{fieldName}' not found in data record.", e);
            }
        }
        /// <summary>
        /// Returns a <see cref="System.DateTime"/> value with the given <see paramref="fieldName"/>.
        /// </summary>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field.</param>
        /// <returns>DateTime Value in the given field.</returns>
        /// <exception cref="DataException"/>
        /// <remarks>An explicit function like this is needed in cases where to connector infers a type that is undesirable.
        /// An example here is Npgsql.NodaTime, which makes Npgsql return Noda type and consequently Orleans is not able to
        /// use it since it expects .NET <see cref="System.DateTime"/>. This function throws if the given <see paramref="fieldName"/> does not exist.</remarks>
        public static DateTime GetDateTimeValue(this IDataRecord record, string fieldName)
        {
            try
            {
                var ordinal = record.GetOrdinal(fieldName);
                return record.GetDateTime(ordinal);
            }
            catch (IndexOutOfRangeException e)
            {
                throw new DataException($"Field '{fieldName}' not found in data record.", e);
            }
        }

        /// <summary>
        /// Returns a value with the given <see paramref="fieldName"/> as int.
        /// </summary>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field.</param>
        /// <exception cref="DataException"/>
        /// <returns>Integer value in the given field indicated by <see paramref="fieldName"/>.</returns>
        public static int GetInt32(this IDataRecord record, string fieldName)
        {
            try
            {
                var ordinal = record.GetOrdinal(fieldName);
                return record.GetInt32(ordinal);
            }
            catch (IndexOutOfRangeException e)
            {
                throw new DataException($"Field '{fieldName}' not found in data record.", e);
            }
        }

        /// <summary>
        /// Returns a value with the given <see paramref="fieldName"/> as long.
        /// </summary>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field.</param>
        /// <exception cref="DataException"/>
        /// <returns>Integer value in the given field indicated by <see paramref="fieldName"/>.</returns>
        public static long GetInt64(this IDataRecord record, string fieldName)
        {
            try
            {
                var ordinal = record.GetOrdinal(fieldName);
                // Original casting when old schema is used.  Here to maintain backwards compatibility
                return record.GetFieldType(ordinal) == typeof(int) ? record.GetInt32(ordinal) : record.GetInt64(ordinal);
            }
            catch (IndexOutOfRangeException e)
            {
                throw new DataException($"Field '{fieldName}' not found in data record.", e);
            }
        }

        /// <summary>
        /// Returns a value with the given <see paramref="fieldName"/> as nullable int.
        /// </summary>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field.</param>
        /// <exception cref="DataException"/>
        /// <returns>Nullable int value in the given field indicated by <see paramref="fieldName"/>.</returns>
        public static int? GetNullableInt32(this IDataRecord record, string fieldName)
        {
            try
            {
                var ordinal = record.GetOrdinal(fieldName);
                var value = record.GetValue(ordinal);
                if (value == DBNull.Value)
                    return null;

                return Convert.ToInt32(value);
            }
            catch (IndexOutOfRangeException e)
            {
                throw new DataException($"Field '{fieldName}' not found in data record.", e);
            }
        }

        /// <summary>
        /// Returns a value with the given <see paramref="fieldName"/>.
        /// </summary>
        /// <typeparam name="TValue">The type of value to retrieve.</typeparam>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field.</param>
        /// <param name="cancellationToken">The cancellation token. Defaults to <see cref="CancellationToken.None"/>.</param>
        /// <returns>Value in the given field indicated by <see paramref="fieldName"/>.</returns>
        /// <exception cref="DataException"/>
        /// <remarks>This function throws if the given <see paramref="fieldName"/> does not exist.</remarks>        
        public static async Task<TValue> GetValueAsync<TValue>(this DbDataReader record, string fieldName, CancellationToken cancellationToken = default)
        {
            try
            {
                var ordinal = record.GetOrdinal(fieldName);
                return await record.GetFieldValueAsync<TValue>(ordinal, cancellationToken).ConfigureAwait(false);
            }
            catch (IndexOutOfRangeException e)
            {
                throw new DataException($"Field '{fieldName}' not found in data record.", e);
            }
        }



        /// <summary>
        /// Adds given parameters to a command using reflection.
        /// </summary>
        /// <typeparam name="T">The type of the parameters.</typeparam>
        /// <param name="command">The command.</param>
        /// <param name="parameters">The parameters.</param>
        /// <param name="nameMap">Maps a given property name to another one defined in the map.</param>
        /// <remarks>Does not support collection parameters currently. Does not cache reflection results.</remarks>
        public static void ReflectionParameterProvider<T>(this IDbCommand command, T parameters, IReadOnlyDictionary<string, string> nameMap = null)
        {
            if (!EqualityComparer<T>.Default.Equals(parameters, default))
            {
                var properties = parameters.GetType().GetProperties();
                for (int i = 0; i < properties.Length; ++i)
                {
                    var property = properties[i];
                    var value = property.GetValue(parameters, null);
                    var parameter = command.CreateParameter();
                    parameter.Value = value ?? DBNull.Value;
                    parameter.Direction = ParameterDirection.Input;
                    parameter.ParameterName = nameMap != null && nameMap.ContainsKey(properties[i].Name) ? nameMap[property.Name] : properties[i].Name;
                    parameter.DbType = typeMap[property.PropertyType];

                    command.Parameters.Add(parameter);
                }
            }
        }


        /// <summary>
        /// Creates object of the given type from the results of a query.
        /// </summary>
        /// <typeparam name="TResult">The type to construct.</typeparam>
        /// <param name="record">The record from which to read the results.</param>
        /// <returns>And object of type <see typeparam="TResult"/>.</returns>
        /// <remarks>Does not support <see typeparam="TResult"/> of type <em>dynamic</em>.</remarks>
        public static TResult ReflectionSelector<TResult>(this IDataRecord record)
        {
            //This is done like this in order to box value types.
            //Otherwise property.SetValue() would have a copy of the struct, which would
            //get garbage collected. Consequently the original struct value would not be set.            
            object obj = Activator.CreateInstance<TResult>();
            var properties = obj.GetType().GetProperties();
            for (int i = 0; i < properties.Length; ++i)
            {
                var rp = record[properties[i].Name];
                if (!Equals(rp, DBNull.Value))
                {
                    properties[i].SetValue(obj, rp, null);
                }
            }

            return (TResult)obj;
        }
    }
}
