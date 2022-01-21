using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Orleans.Providers;
using Orleans.SqlUtils.StorageProvider.GrainInterfaces;

namespace Orleans.SqlUtils.StorageProvider.GrainClasses
{
    [StorageProvider(ProviderName = "SqlStore")]
    public class CustomerGrain : Grain<CustomerState>, ICustomerGrain
    {
        private readonly Random _random = new Random();

        public override async Task OnActivateAsync(CancellationToken cancellationToken)
        {
            await base.OnActivateAsync(cancellationToken);
        }

        public Task<string> IntroduceSelf()
        {
            return Task.FromResult(string.Format("Hello, my name is {0} {1}", State.FirstName, State.LastName));
        }

        public async Task Set(int customerId, string firstName, string lastName)
        {
            State.CustomerId = customerId;
            State.FirstName = firstName;
            State.LastName = lastName;

            await WriteStateAsync();
        }

        public async Task AddDevice(IDeviceGrain device)
        {
            if (device == null)
                throw new ArgumentNullException("device");

            if (null == State.Devices)
                State.Devices = new List<IDeviceGrain>();

            if (!State.Devices.Contains(device))
            {
                State.Devices.Add(device);
                await device.SetOwner(this);
            }

            await WriteStateAsync();
        }

        public async Task SetRandomState()
        {
            int customerId = (int)this.GetPrimaryKeyLong();
            
            var dt = DateTime.UtcNow;
            var now = new DateTime(dt.Year, dt.Month, dt.Day, dt.Hour, dt.Minute, dt.Second, DateTimeKind.Utc);

            State.CustomerId = customerId;
            State.FirstName = "FirstName_" + customerId;
            State.LastName = "LastName_" + customerId;
            State.NickName = "NickName_" + customerId;
            State.BirthDate = new DateTime(_random.Next(40) + 1970, _random.Next(12) + 1,  _random.Next(28) + 1, 0, 0, 0, DateTimeKind.Utc);
            State.Gender = _random.Next(2);
            State.Country = "Country_" + _random.Next();
            State.AvatarUrl = "AvatarUrl_" + _random.Next();
            State.KudoPoints = _random.Next();
            State.Status = _random.Next();
            State.LastLogin = now;
            State.Devices = new List<IDeviceGrain>();

            await WriteStateAsync();
        }
    }
}
