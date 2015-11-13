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
using Orleans.SqlUtils;

namespace UnitTests.General
{
    internal class MySqlStorageForTesting : RelationalStorageForTesting
    {
        public MySqlStorageForTesting(string connectionString) : base(AdoNetInvariants.InvariantNameMySql, connectionString)
        {
        }

        public IEnumerable<string> SplitScript(string setupScript)
        {
            return setupScript.Replace("END$$", "END;")
                .Split(new[] {"DELIMITER $$", "DELIMITER ;"}, StringSplitOptions.RemoveEmptyEntries);
        }

        protected override string CreateDatabaseTemplate
        {
            get { return @"CREATE DATABASE `{0}`"; }
        }

        protected override string DropDatabaseTemplate
        {
            get { return @"DROP DATABASE `{0}`"; }
        }

        public override string DefaultConnectionString
        {
            get { return "Server=127.0.0.1;Database=sys; Uid=root;Pwd=root;"; }
        }

        protected override string SetupSqlScriptFileName
        {
            get { return "CreateOrleansTables_MySql.sql"; }
        }

        protected override string ExistsDatabaseTemplate
        {
            get { return "SELECT COUNT(1) FROM INFORMATION_SCHEMA.SCHEMATA WHERE SCHEMA_NAME = '{0}'"; }
        }

        protected override IEnumerable<string> ConvertToExecutableBatches(string setupScript, string databaseName)
        {
            return setupScript.Replace("END$$", "END;")
                .Split(new[] { "DELIMITER $$", "DELIMITER ;" }, StringSplitOptions.RemoveEmptyEntries);
        }
    }
}