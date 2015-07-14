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
