using System;
using System.Collections.Generic;
using System.Data;
using Newtonsoft.Json;
using Orleans.SqlUtils.StorageProvider.GrainClasses;

namespace Orleans.SqlUtils.StorageProvider.Tests
{
    /// <summary>
    /// Sample grains' state map. Potential for improvement and usage of auto mapping libraries
    /// </summary>
    internal class SampleGrainStateMapFactory : IGrainStateMapFactory
    {
        public GrainStateMap CreateGrainStateMap()
        {
            var jsonSettings = new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.All
            };

            GrainStateMap gsm = new GrainStateMap();
            gsm
                .Register<CustomerGrain>(
                    (cmd, dt) =>
                    {
                        cmd.CommandText = 
                            @"SELECT G.GrainKey, CustomerId, FirstName, LastName, NickName, BirthDate, Gender, Country, AvatarUrl, KudoPoints, Status, LastLogin, Devices " +
                            @"FROM dbo.CustomerGrains AS G JOIN @List as L "+
                            @"ON G.GrainKey = L.GrainKey ";
                        var p = cmd.Parameters.AddWithValue("@List", dt);
                        p.SqlDbType = SqlDbType.Structured;
                        p.TypeName = "dbo.GrainKeyListType"; // ?
                    },
                    reader =>
                    {
                        var devices = reader["Devices"];
                        if (devices is DBNull)
                            devices = string.Empty;
                        return new
                        {
                            CustomerId = reader["CustomerId"],
                            FirstName = reader["FirstName"],
                            LastName = reader["LastName"],
                            NickName = reader["NickName"],
                            BirthDate = reader["BirthDate"],
                            Gender = reader["Gender"],
                            Country = reader["Country"],
                            AvatarUrl = reader["AvatarUrl"],
                            KudoPoints = reader["KudoPoints"],
                            Status = reader["Status"],
                            LastLogin = reader["LastLogin"],
                            Devices = JsonConvert.DeserializeObject((string)devices, jsonSettings)
                        };
                    },
                    entries =>
                    {
                        DataTable data = new DataTable();
                        data.Columns.Add("GrainKey", typeof(string));
                        data.Columns.Add("CustomerId", typeof(int));
                        data.Columns.Add("FirstName", typeof(string));
                        data.Columns.Add("LastName", typeof(string));
                        data.Columns.Add("NickName", typeof(string));
                        data.Columns.Add("BirthDate", typeof(DateTime));
                        data.Columns.Add("Gender", typeof(int));
                        data.Columns.Add("Country", typeof(string));
                        data.Columns.Add("AvatarUrl", typeof(string));
                        data.Columns.Add("KudoPoints", typeof(int));
                        data.Columns.Add("Status", typeof(int));
                        data.Columns.Add("LastLogin", typeof(DateTime));
                        data.Columns.Add("Devices", typeof(string));
                        foreach (var entry in entries)
                        {
                            data.Rows.Add(entry.GrainIdentity.GrainKey, JsonConvert.SerializeObject(entry.State, jsonSettings));
                        }
                        return data;
                    },
                    (cmd, dt) =>
                    {
                        cmd.CommandText = @"Upsert_CustomerGrains";
                        cmd.CommandType = CommandType.StoredProcedure;

                        var p = cmd.Parameters.AddWithValue("@List", dt);
                        p.SqlDbType = SqlDbType.Structured;
                        p.TypeName = "dbo.CustomerGrainsType";
                    }
                );

            return gsm;
        }
    }
}
