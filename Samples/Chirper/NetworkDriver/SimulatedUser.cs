using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.Samples.Chirper.GrainInterfaces;

namespace Orleans.Samples.Chirper.Network.Driver
{
    class SimulatedUser : IDisposable
    {
        public double ShouldRechirpRate { get; set; }
        public int ChirpPublishTimebase { get; set; }
        public bool ChirpPublishTimeRandom { get; set; }
        public bool Verbose { get; set; }

        readonly IChirperAccount user;
        readonly Task<long> getUserIdAsync;
        long userId;

        public SimulatedUser(IChirperAccount user)
        {
            this.user = user;
            this.getUserIdAsync = user.GetUserId();
        }

        public async void Start()
        {
            this.userId = await getUserIdAsync;
            Console.WriteLine("Starting simulating Chirper user id=" + userId);
        }

        public void Stop()
        {
            Console.WriteLine("Stopping simulating Chirper user id=" + userId);
        }

        #region IDisposable interface

        public void Dispose()
        {
            Stop();
        }
        #endregion

        public Task PublishMessage(string message)
        {
            return user.PublishMessage(message);
        }
    }
}
