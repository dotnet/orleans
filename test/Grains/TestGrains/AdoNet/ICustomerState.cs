using System;
using System.Collections.Generic;
using Orleans.SqlUtils.StorageProvider.GrainInterfaces;

namespace Orleans.SqlUtils.StorageProvider.GrainClasses
{
    [Serializable]
    [GenerateSerializer]
    public class CustomerState
    {
        [Id(0)]
        public int CustomerId { get; set; }
        [Id(1)]
        public string FirstName { get; set; }
        [Id(2)]
        public string LastName { get; set; }
        [Id(3)]
        public string NickName { get; set; }
        [Id(4)]
        public DateTime BirthDate { get; set; }
        [Id(5)]
        public int Gender { get; set; }
        [Id(6)]
        public string Country { get; set; }
        [Id(7)]
        public string AvatarUrl { get; set; }
        [Id(8)]
        public int KudoPoints { get; set; }
        [Id(9)]
        public int Status { get; set; }
        [Id(10)]
        public DateTime LastLogin { get; set; }
        [Id(11)]
        public List<IDeviceGrain> Devices { get; set; }
    }
}