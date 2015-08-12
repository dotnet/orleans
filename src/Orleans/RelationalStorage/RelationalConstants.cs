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
using System.Data.Common;
using System.Diagnostics;
using System.Linq;


namespace Orleans.Runtime.Storage.Relational
{
    /// <summary>
    /// A holder for well known, vendor specific connector class invariant names.
    /// </summary>
    public static class WellKnownRelationalInvariants
    {
        /// <summary>
        /// Microsoft SQL Server.
        /// </summary>
        public const string SqlServer = "System.Data.SqlClient";

        /// <summary>
        /// Oracle Database server.
        /// </summary>
        public const string OracleDatabase = "Oracle.DataAccess.Client";
    }


    /// <summary>
    /// Constants Orleans uses to relational database management.
    /// </summary>
    [DebuggerDisplay("InvariantName = {InvariantName}, AssemblyQualifiedName = {AssemblyQualifiedName}")]
    public class RelationalConstants
    {
        /// <summary>
        /// A key for a well known constant, likely set by a database connector vendor.
        /// </summary>
        public const string NameKey = "Name";

        /// <summary>
        /// A key for a well known constant, likey set by a database connector vendor.
        /// </summary>
        public const string DescriptionKey = "Description";

        /// <summary>
        /// A key for a well known constant, likely set by a database connector vendor.
        /// </summary>
        public const string InvariantNameKey = "InvariantName";

        /// <summary>
        /// A key for a well known constant, likely set by a database connector vendor.
        /// </summary>
        public const string AssemblyQualifiedNameKey = "AssemblyQualifiedName";

        /// <summary>
        /// A key to add a default connection string if none otherwise defined.
        /// This is something a given database will be listening to. If not altered during
        /// setup, usually databases have well known default settings.
        /// </summary>
        /// <remarks>Vendor specific.</remarks>
        public const string DefaultConnectionStringKey = "DefaultConnectionString";

        /// <summary>
        /// The character that indicates a parameter.
        /// </summary>
        /// <remarks>Vendor specific.</remarks>
        public const string ParameterIndicatorKey = "ParameterIndicator";

        /// <summary>
        /// The character that indicates a start escape key for columns and tables that are reserved words.
        /// </summary>
        /// <remarks>Vendor specific.</remarks>
        public const string StartEscapeIndicatorKey = "StartEscapeIndicator";

        /// <summary>
        /// The character that indicates an end escape key for columns and tables that are reserved words.
        /// </summary>
        /// <remarks>Vendor specific.</remarks>
        public const string EndEscapeIndicatorKey = "EndEscapeIndicator";

        /// <summary>
        /// A key for a query template to create a database with a given name.
        /// </summary>
        /// <remarks>Vendor specific.</remarks>
        public const string CreateDatabaseKey = "CreateDatabaseTemplate";

        /// <summary>
        /// A key for a query template to drop a database with a given name.
        /// </summary>
        /// <remarks>Has vendor specific elements.</remarks>
        public const string DropDatabaseKey = "DropDatabaseTemplate";

        /// <summary>
        /// A key for a query template to delete all data in tables.
        /// </summary>
        /// <remarks>Truncating would be more efficient, but is more vendor specific due to constraints.</remarks>
        public const string DeleteAllDataKey = "DeleteAllDataTemplate";

        /// <summary>
        /// A key for a query template if a database with a given name exists.
        /// </summary>
        /// <remarks>Vendor specific.</remarks>
        public const string ExistsDatabaseKey = "ExistsDatabaseTemplate";

        /// <summary>
        /// A collection of the well known keys Orleans uses to database management.
        /// </summary>
        public static IReadOnlyCollection<string> Keys = new ReadOnlyCollection<string>(new[]
        {
            NameKey,
            DescriptionKey,
            InvariantNameKey,
            AssemblyQualifiedNameKey,
            DefaultConnectionStringKey,
            ParameterIndicatorKey,
            StartEscapeIndicatorKey,
            EndEscapeIndicatorKey,
            CreateDatabaseKey,
            DropDatabaseKey,
            DeleteAllDataKey,
            ExistsDatabaseKey,
        });

        /// <summary>
        /// A parameter indicator that can be used in queries which is then substituted according to the
        /// underlying database.
        /// </summary>
        public const string InvariantParameterIndicator = "@";

        /// <summary>
        /// The factory constants that are used to augmend data found by <see cref="DbProviderFactories"/> and then
        /// fill the found data back to constants.
        /// </summary>
        private static Dictionary<string, Dictionary<string, string>> FactoryConstants = new Dictionary<string, Dictionary<string, string>>();


        static RelationalConstants()
        {
            //This is probably the same for all the databases.
            const string DeleteAllDataTemplate =
                @"DELETE OrleansStatisticsTable;
                  DELETE OrleansClientMetricsTable;
                  DELETE OrleansSiloMetricsTable;
                  DELETE OrleansRemindersTable;
                  DELETE OrleansMembershipTable;
                  DELETE OrleansMembershipVersionTable;";

            //Microsoft SQL Server.
            AddOrModifyFactoryConstant(WellKnownRelationalInvariants.SqlServer, StartEscapeIndicatorKey, "[");
            AddOrModifyFactoryConstant(WellKnownRelationalInvariants.SqlServer, StartEscapeIndicatorKey, "]");
            AddOrModifyFactoryConstant(WellKnownRelationalInvariants.SqlServer, DefaultConnectionStringKey, @"Data Source=(localdb)\MSSQLLocalDB;Database=Master;Integrated Security=True;Asynchronous Processing=True;Max Pool Size=200; MultipleActiveResultSets=True");
            AddOrModifyFactoryConstant(WellKnownRelationalInvariants.SqlServer, ParameterIndicatorKey, "@");
            AddOrModifyFactoryConstant(WellKnownRelationalInvariants.SqlServer, ExistsDatabaseKey, "SELECT CAST(COUNT(1) AS BIT) FROM sys.databases WHERE name = @databaseName");
            AddOrModifyFactoryConstant(WellKnownRelationalInvariants.SqlServer, CreateDatabaseKey,
                @"USE [Master];
                DECLARE @fileName AS NVARCHAR(255) = CONVERT(NVARCHAR(255), SERVERPROPERTY('instancedefaultdatapath')) + N'{0}';
                EXEC('CREATE DATABASE [{0}] ON PRIMARY 
                (
                    NAME = [{0}], 
                    FILENAME =''' + @fileName + ''', 
                    SIZE = 5MB, 
                    MAXSIZE = 100MB, 
                    FILEGROWTH = 5MB
                )')");
            AddOrModifyFactoryConstant(WellKnownRelationalInvariants.SqlServer, DropDatabaseKey,
                @"USE [Master]; ALTER DATABASE [{0}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{0}];");
            AddOrModifyFactoryConstant(WellKnownRelationalInvariants.SqlServer, DeleteAllDataKey, DeleteAllDataTemplate);            
        }


        /// <summary>
        /// Adds data to factory constants or modifies it. This data is used to augmented data from <see cref="GetRelationalConstants"/>.
        /// </summary>
        /// <param name="invariantName">A well known database provider invariant name.</param>
        /// <param name="key">One of the keys in <see cref="Keys"/>.</param>
        /// <param name="value">The value to add.</param>
        /// <returns><em>TRUE</em> if the value was added, <em>FALSE</em> if modified.</returns>
        public static bool AddOrModifyFactoryConstant(string invariantName, string key, string value)
        {
            if(string.IsNullOrWhiteSpace(invariantName))
            {
                throw new ArgumentException("The name of invariant must contain characters", "invariantName");
            }

            if(!Keys.Contains(key))
            {
                throw new ArgumentException(string.Format("The key \"{0}\" must be one defined in the RelationalConstants.Keys collection", key), "key");
            }

            if(!FactoryConstants.ContainsKey(invariantName))
            {
                FactoryConstants.Add(invariantName, new Dictionary<string, string>());
            }

            bool isAdded = !FactoryConstants[invariantName].ContainsKey(key);
            if(isAdded)
            {
                FactoryConstants[invariantName].Add(key, value);
            }
            else
            {
                FactoryConstants[invariantName][key] = value;
            }

            return isAdded;
        }


        /// <summary>
        /// Seeks for database provider factory classes from GAC or as indicated by
        /// the configuration file, see at <see href="https://msdn.microsoft.com/en-us/library/dd0w4a2z%28v=vs.110%29.aspx">Obtaining a DbProviderFactory</see>.
        /// </summary>
        /// <returns>Database constants with values from <see cref="DbProviderFactories"/> augmented with other predefined data in <see cref="RelationalConstants"/>.</returns>
        /// <remarks>Every call may potentially update data as it is refreshed from <see cref="DbProviderFactories"/>.</remarks>
        public static IEnumerable<RelationalConstants> GetRelationalConstants()
        {
            //This method seeks for factory classes either from the GAC or as indicated in a config file.            
            var factoryData = DbProviderFactories.GetFactoryClasses();

            //The provided default information will be loaded from here to the factory constants
            //which are further augmented with predefined query templates.
            foreach(DataRow row in factoryData.Rows)
            {
                var invariantName = row[InvariantNameKey].ToString();
                AddOrModifyFactoryConstant(invariantName, InvariantNameKey, invariantName);
                AddOrModifyFactoryConstant(invariantName, NameKey, row[NameKey].ToString());
                AddOrModifyFactoryConstant(invariantName, DescriptionKey, row[DescriptionKey].ToString());
                AddOrModifyFactoryConstant(invariantName, AssemblyQualifiedNameKey, row[AssemblyQualifiedNameKey].ToString());
            }

            //And last, the constant instances are created, filled and returned.
            return FactoryConstants.Select(i => new RelationalConstants(
                i.Value[InvariantNameKey],
                i.Value[NameKey],
                i.Value[DescriptionKey],
                i.Value[AssemblyQualifiedNameKey],
                i.Value.GetValueOrDefault(DefaultConnectionStringKey),
                i.Value.GetValueOrDefault(CreateDatabaseKey),
                i.Value.GetValueOrDefault(ExistsDatabaseKey)));
        }


        /// <summary>
        /// Gets a constant if defined.
        /// </summary>
        /// <param name="invariantName">The invariant with which to use the key.</param>
        /// <param name="key">The key with which to retrieve the value.</param>
        /// <returns>A defined constant value.</returns>
        /// <exception cref="ArgumentException"> if either <see paramref="invariantName"/> or <see paramref="key"/> is not found.</exception>
        public static string GetConstant(string invariantName, string key)
        {
            if(!FactoryConstants.ContainsKey(invariantName))
            {
                throw new ArgumentException("Invariant not defined", "invariantName");
            }

            if(!FactoryConstants[invariantName].ContainsKey(key))
            {
                throw new ArgumentException(string.Format("Key not defined for {0}", invariantName), "key");
            }

            return FactoryConstants[invariantName][key];
        }


        /// <summary>
        /// A well known database provider invariant name.
        /// </summary>            
        public string InvariantName { get; private set; }

        /// <summary>
        /// The name of the database connector.
        /// </summary>
        public string Name { get; private set; }

        /// <summary>
        /// Database connector description.
        /// </summary>
        public string Description { get; private set; }

        /// <summary>
        /// Assembly qualified name of the database connector.
        /// </summary>
        public string AssemblyQualifiedName { get; private set; }

        /// <summary>
        /// A default connection string to be used if not otherwise provided.
        /// </summary>
        public string DefaultConnectionString { get; private set; }

        /// <summary>
        /// A template to create a database with a name given as a parameter.
        /// </summary>
        public string CreateDatabaseTemplate { get; private set; }

        /// <summary>
        /// A template to check existence of a database with a name given as a parameter.
        /// </summary>
        public string ExistsDatabaseTemplate { get; private set; }


        /// <summary>
        /// Constructor for instance of constant values to database as indicated by <see cref="InvariantName"/>.
        /// </summary>
        /// <param name="invariantName">A well known database provider invariant name.</param>
        /// <param name="name">The name of the database connector.</param>
        /// <param name="description">Database connector description.</param>
        /// <param name="assemblyQualifiedName">Assembly qualified name of the database connector.</param>
        /// <param name="defaultConnectionString">A default connection string to be used if not otherwise provided.</param>
        /// <param name="createDatabaseTemplate">A template to create a database with a name given as a parameter.</param>
        /// <param name="existsDatabaseTemplate">A template to check existence of a database with a name given as a parameter.</param>
        public RelationalConstants(
            string invariantName,
            string name,
            string description,
            string assemblyQualifiedName,
            string defaultConnectionString,
            string createDatabaseTemplate,
            string existsDatabaseTemplate)
        {
            if(string.IsNullOrWhiteSpace(invariantName))
            {
                throw new ArgumentException("Parameter must contain at least one non-whitespace character", "invariantName");
            }

            if(string.IsNullOrWhiteSpace(name))
            {
                throw new ArgumentException("Parameter must contain at least one non-whitespace character", "name");
            }

            if(string.IsNullOrWhiteSpace(description))
            {
                throw new ArgumentException("Parameter must contain at least one non-whitespace character", "description");
            }

            if(string.IsNullOrWhiteSpace(assemblyQualifiedName))
            {
                throw new ArgumentException("Parameter must contain at least one non-whitespace character", "assemblyQualifiedName");
            }

            InvariantName = invariantName;
            Name = name;
            Description = description;
            AssemblyQualifiedName = assemblyQualifiedName;
            DefaultConnectionString = defaultConnectionString;
            CreateDatabaseTemplate = createDatabaseTemplate;
            ExistsDatabaseTemplate = existsDatabaseTemplate;
        }


        public override string ToString()
        {
            return string.Format("RelationalConstants\n{0}: {1}\n{2}: {3}", InvariantNameKey, InvariantName, AssemblyQualifiedNameKey, AssemblyQualifiedName);
        }
    }
}
