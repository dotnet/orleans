/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Data;

namespace Orleans.SqlUtils
{
    /// <summary>
    /// Contains some convenience methods to use in conjunction with <see cref="IRelationalStorage">IRelationalStorage</see> and <see cref="RelationalStorage">GenericRelationalStorage</see>.
    /// </summary>
    public static class DbExtensions
    {
        /// <summary>
        /// An explicit map of type CLR viz database type conversions.
        /// </summary>
        /// <summary>
        /// An explicit map of type CLR viz database type conversions.
        /// </summary>
        static readonly ReadOnlyDictionary<Type, DbType> typeMap = new ReadOnlyDictionary<Type, DbType>(new Dictionary<Type, DbType>
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
        /// Returns a value if it is not <see cref="System.DBNull"/>, <em>default(TValue)</em> otherwise.
        /// </summary>
        /// <typeparam name="TValue">The type of the value to request.</typeparam>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field to retrieve.</param>
        /// <param name="default">The default value if value in position is <see cref="System.DBNull"/>.</param>
        /// <returns>Either the given value or the default for the requested type.</returns>
        /// <exception cref="IndexOutOfRangeException"/>
        /// <remarks>This function throws if the given <see paramref="fieldName"/> does not exist.</remarks>
        public static TValue GetValueOrDefault<TValue>(this IDataRecord record, string fieldName, TValue @default = default(TValue))
        {
            var ordinal = record.GetOrdinal(fieldName);
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
        /// <remarks>This function throws if the given <see paramref="fieldName"/> does not exist.</remarks>        
        public static TValue GetValueOrDefault<TValue>(this IDataRecord record, int ordinal, TValue @default = default(TValue))
        {
            return record.IsDBNull(ordinal) ? @default : (TValue)record.GetValue(ordinal);
        }


        /// <summary>
        /// Returns a value with the given <see paramref="fieldName"/>.
        /// </summary>
        /// <typeparam name="TValue">The type of value to retrieve.</typeparam>
        /// <param name="record">The record from which to retrieve the value.</param>
        /// <param name="fieldName">The name of the field.</param>
        /// <returns>Value in the given field indicated by <see paramref="fieldName"/>.</returns>
        /// <exception cref="IndexOutOfRangeException"/>
        /// <remarks>This function throws if the given <see paramref="fieldName"/> does not exist.</remarks>        
        public static TValue GetValue<TValue>(this IDataRecord record, string fieldName)
        {
            var ordinal = record.GetOrdinal(fieldName);
            return (TValue)record.GetValue(ordinal);
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
            if(!EqualityComparer<T>.Default.Equals(parameters, default(T)))
            {
                var properties = parameters.GetType().GetProperties();
                for(int i = 0; i < properties.Length; ++i)
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
            for(int i = 0; i < properties.Length; ++i)
            {
                var rp = record[properties[i].Name];
                if(!Equals(rp, DBNull.Value))
                {
                    properties[i].SetValue(obj, rp, null);
                }
            }

            return (TResult)obj;
        }
    }
}
