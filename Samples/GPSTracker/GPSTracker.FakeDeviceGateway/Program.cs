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
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace GPSTracker.FakeDeviceGateway
{
    class Program
    {
        static int counter = 0;
        static Random rand = new Random();

        // San Francisco: approximate boundaries.
        const double SFLatMin = 37.708;
        const double SFLatMax = 37.78;
        const double SFLonMin = -122.50;
        const double SFLonMax = -122.39;

        static void Main(string[] args)
        {
            GrainClient.Initialize("LocalConfiguration.xml");

            // simulate 20 devices
            var devices = new List<Model>();
            for (var i = 0; i < 20; i++)
            {
                devices.Add(new Model()
                {
                    DeviceId = i,
                    Lat = rand.NextDouble(SFLatMin, SFLatMax),
                    Lon = rand.NextDouble(SFLonMin, SFLonMax),
                    Direction = rand.NextDouble(-Math.PI, Math.PI),
                    Speed = rand.NextDouble(0, 0.0005)
                });
            }

            var timer = new System.Timers.Timer();
            timer.Interval = 1000;
            timer.Elapsed += (s, e) =>
            {
                Console.Write(". ");
                Interlocked.Exchange(ref counter, 0);
            };
            timer.Start();

            // create a thread for each device, and continually move it's position
            foreach (var model in devices)
            {
                var ts = new ThreadStart(() =>
                {
                    while (true)
                    {
                        try
                        {
                            SendMessage(model).Wait();
                            Thread.Sleep(rand.Next(500, 2500));
                        }
                        catch (Exception ex)
                        {
                            Console.WriteLine(ex.ToString());
                        }

                    }
                });
                new Thread(ts).Start();
            }
        }

        private static async Task SendMessage(Model model)
        {
            // simulate the device moving
            model.Speed += rand.NextDouble(-0.0001, 0.0001);
            model.Direction += rand.NextDouble(-0.001, 0.001);

            var lastLat = model.Lat;
            var lastLon = model.Lon;

            UpdateDevicePosition(model);

            if (lastLat == model.Lat || lastLon == model.Lon)
            {
                // the device has hit the boundary, so reverse it's direction
                model.Speed = -model.Speed;
                UpdateDevicePosition(model);
            }

            // send the mesage to Orleans
            var device = GrainClient.GrainFactory.GetGrain<IDeviceGrain>(model.DeviceId);
            await device.ProcessMessage(new DeviceMessage(model.Lat, model.Lon, counter, model.DeviceId, DateTime.UtcNow));
            Interlocked.Increment(ref counter);
        }

        private static void UpdateDevicePosition(Model model)
        {
            model.Lat += Math.Cos(model.Direction) * model.Speed;
            model.Lon += Math.Sin(model.Direction) * model.Speed;
            model.Lat = model.Lat.Cap(SFLatMin, SFLatMax);
            model.Lon = model.Lon.Cap(SFLonMin, SFLonMax);
        }

        class Model
        {
            public int DeviceId { get; set; }
            public double Lat { get; set; }
            public double Lon { get; set; }
            public double Direction { get; set; }
            public double Speed { get; set; }
        }

    }
}
