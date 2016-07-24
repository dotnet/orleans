using System;
using System.Collections.Generic;
using Orleans;
using Orleans.SqlUtils.StorageProvider.GrainInterfaces;

namespace Orleans.SqlUtils.StorageProvider.GrainClasses
{
    [Serializable]
    public class CustomerState
    {
        public int CustomerId { get; set; }
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public string NickName { get; set; }
        public DateTime BirthDate { get; set; }
        public int Gender { get; set; }
        public string Country { get; set; }
        public string AvatarUrl { get; set; }
        public int KudoPoints { get; set; }
        public int Status { get; set; }
        public DateTime LastLogin { get; set; }
        public List<IDeviceGrain> Devices { get; set; }
    }
}