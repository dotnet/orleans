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

using GPSTracker.Common;
using GPSTracker.GrainInterface;
using Orleans;
using Orleans.Concurrency;
using System;
using System.Threading.Tasks;

namespace GPSTracker.GrainImplementation
{
    /// <summary>
    /// Orleans grain implementation class.
    /// </summary>
    [Reentrant]
    public class DeviceGrain : Orleans.Grain, IDeviceGrain
    {
        public DeviceMessage LastMessage { get; set; }

        public async Task ProcessMessage(DeviceMessage message)
        {
            if (null == this.LastMessage || this.LastMessage.Latitude != message.Latitude || this.LastMessage.Longitude != message.Longitude)
            {
                // only sent a notification if the position has changed
                var notifier = GrainFactory.GetGrain<IPushNotifierGrain>(0);
                var speed = GetSpeed(this.LastMessage, message);

                // record the last message
                this.LastMessage = message;

                // forward the message to the notifier grain
                var velocityMessage = new VelocityMessage(message, speed);
                await notifier.SendMessage(velocityMessage);
            }
            else
            {
                // the position has not changed, just record the last message
                this.LastMessage = message;
            }
        }

        static double GetSpeed(DeviceMessage message1, DeviceMessage message2)
        {
            // calculate the speed of the device, using the interal state of the grain
            if (message1 == null) return 0;
            if (message2 == null) return 0;

            const double R = 6371 * 1000;
            var x = (message2.Longitude - message1.Longitude) * Math.Cos((message2.Latitude + message1.Latitude) / 2);
            var y = message2.Latitude - message1.Latitude;
            var distance = Math.Sqrt(Math.Pow(x, 2) + Math.Pow(y, 2)) * R;
            var time = (message2.Timestamp - message1.Timestamp).TotalSeconds;
            if (time == 0) return 0;
            return distance / time;
        }

    }
}
