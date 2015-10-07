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

namespace Orleans.Runtime.Storage.Relational
{
    /// <summary>
    /// This class holds a bag of operational constants Orleans uses, such as queries.
    /// </summary>
    public class QueryConstantsBag
    {
        /// <summary>
        /// The loaded query constants by database invariant key and query key.
        /// </summary>
        private readonly Dictionary<string, Dictionary<string, string>> constants = new Dictionary<string, Dictionary<string, string>>();

        /// <summary>
        /// A default constructor.
        /// </summary>
        public QueryConstantsBag()
        {
            //This is a bootstrap query used to load other queries from a database.
            const string OrleansQueries = @"SELECT QueryKey, QueryText FROM OrleansQuery;";
            AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameSqlServer, QueryKeys.OrleansQueriesKey, OrleansQueries);
            AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameOracleDatabase, QueryKeys.OrleansQueriesKey, OrleansQueries);
            AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameMySql, QueryKeys.OrleansQueriesKey, OrleansQueries);

            //These are vendor specific constants that are likely never to change. This is the place to add them so that
            //they are readily available when needed.
            AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameSqlServer, RelationalVendorConstants.StartEscapeIndicatorKey, "[");
            AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameSqlServer, RelationalVendorConstants.EndEscapeIndicatorKey, "]");
            AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameSqlServer, RelationalVendorConstants.ParameterIndicatorKey, "@");

            AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameMySql, RelationalVendorConstants.StartEscapeIndicatorKey, "`");
            AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameMySql, RelationalVendorConstants.EndEscapeIndicatorKey, "`");
            AddOrModifyQueryConstant(AdoNetInvariants.InvariantNameMySql, RelationalVendorConstants.ParameterIndicatorKey, "@");
        }


        /// <summary>
        /// Gets all of the loaded constants for a given invariant.
        /// </summary>
        /// <param name="invariantName">The name of the invariant.</param>
        /// <returns>All querys by their keys for a given invariant.</returns>
        public IReadOnlyDictionary<string, string> GetAllConstants(string invariantName)
        {
            return new ReadOnlyDictionary<string, string>(constants[invariantName]);
        }


        /// <summary>
        /// Gets a constant from the bag of constants.
        /// </summary>
        /// <param name="invariantName">The invariant for which to get the constant.</param>
        /// <param name="key">The key with which to get the constant.</param>
        /// <returns>A constant with the given parameters.</returns>
        public string GetConstant(string invariantName, string key)
        {
            if(string.IsNullOrWhiteSpace(invariantName))
            {
                throw new ArgumentException("The name of invariant must contain characters", "invariantName");
            }

            return constants[invariantName][key];
        }


        /// <summary>
        /// Adds data to query constants or modifies it.
        /// </summary>
        /// <param name="invariantName">A well known database provider invariant name.</param>
        /// <param name="key">One of the keys used storing the constant.</param>
        /// <param name="value">The value to add.</param>
        /// <returns><em>TRUE</em> if the value was added, <em>FALSE</em> if modified.</returns>
        public bool AddOrModifyQueryConstant(string invariantName, string key, string value)
        {
            if(string.IsNullOrWhiteSpace(invariantName))
            {
                throw new ArgumentException("The name of invariant must contain characters", "invariantName");
            }

            if(!constants.ContainsKey(invariantName))
            {
                constants.Add(invariantName, new Dictionary<string, string>());
            }

            bool isAdded = !constants[invariantName].ContainsKey(key);
            if(isAdded)
            {
                constants[invariantName].Add(key, value);
            }
            else
            {
                constants[invariantName][key] = value;
            }

            return isAdded;
        }
    }
}
