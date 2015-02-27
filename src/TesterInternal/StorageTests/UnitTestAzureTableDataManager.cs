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
using System.Text;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Data.Services.Common;
using Microsoft.WindowsAzure.Storage.Table;
using Orleans;
using Orleans.AzureUtils;

namespace UnitTests.StorageTests
{
    [Serializable]    
    public class UnitTestAzureTableData : TableEntity
    {
        public byte[] Data { get; set; }
        public string StringData { get; set; }

        public UnitTestAzureTableData()
        {

        }

        public UnitTestAzureTableData(string data, string partitionKey, string rowKey)
        {
            StringData = data;
            PartitionKey = partitionKey;
            RowKey = rowKey;
        }

        public UnitTestAzureTableData Clone()
        {
            return new UnitTestAzureTableData
            {
                StringData = this.StringData,
                PartitionKey = this.PartitionKey,
                RowKey = this.RowKey
            };
        }

        public override string ToString()
        {
            StringBuilder sb = new StringBuilder();
            sb.Append("UnitTestAzureData[");
            sb.Append(" PartitionKey=").Append(PartitionKey);
            sb.Append(" RowKey=").Append(RowKey);
            sb.Append(" ]");
            return sb.ToString();
        }
    }

    internal class UnitTestAzureTableDataManager : AzureTableDataManager<UnitTestAzureTableData>
    {
        protected const string INSTANCE_TABLE_NAME = "UnitTestAzureData";

        public UnitTestAzureTableDataManager(string storageConnectionString)
            : base(INSTANCE_TABLE_NAME, storageConnectionString)
        {
            InitTableAsync().WithTimeout(AzureTableDefaultPolicies.TableCreationTimeout).Wait();
        }
    }
}
