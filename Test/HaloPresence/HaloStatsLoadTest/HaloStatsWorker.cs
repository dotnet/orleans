using System;
using System.Collections.Generic;
using System.Threading;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using Orleans;
using Orleans.RuntimeCore;
using LoadTestBase;
using Microsoft.Halo.Stats.GrainInterfaces.Midnight;
using Microsoft.Halo.Nebula.Utils.Http;
using Corinth.Blf;
using StatsTestCommon;
using TestHelper.Midnight.Helpers;


namespace HaloStatsLoadTest
{
    public class HaloStatsWorker : WorkerBase
    {
        private List<HttpRequestMessageTransport> blobs;
        private List<IGameGrain> grains;
        private int nGrains;
        private int startPoint;

        // This is an example of worker initialization.
        // Pre-create grains, per-allocate data buffers, etc...
        public void ApplicationInitialize(int numGrains)
        {
            BuildFiles(numGrains);
            LoadTestDriverBase.WriteProgress("Done ApplicationInitialize by worker " + Name);
        }

        protected override AsyncCompletion IssueRequest(int requestNumber)
        {
            int index = (requestNumber + startPoint) % nGrains;
            IGameGrain grain = grains[index];
            HttpRequestMessageTransport blob = blobs[index];
            return grain.PostStats(blob);
        }

        private void BuildFiles(int numGrains)
        {
            Random rand = new Random();
            this.nGrains = (numGrains > nRequests) ? (int)nRequests : numGrains;
            this.startPoint = rand.Next(numGrains);
            this.blobs = new List<HttpRequestMessageTransport>();
            this.grains = new List<IGameGrain>();
            
            const int numPlayers = 16;
            for (int i = 0; i < numGrains; i++)
            {
                CompetitiveBlf blf = new CompetitiveBlf(numPlayers, 2);
                BlfFile file = blf.Generate();
                //GameGrain -> blf.GameId;
                byte[] compressedBlf = SerializationHelper.GetCompressedBytes(file);

                HttpRequestMessage foo = new HttpRequestMessage();
                foo.Content = new ByteArrayContent(compressedBlf);
                foo.Content.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
                foo.Headers.Host = "www.w3.org";
                HttpRequestMessageTransport sendThis = foo.ToTransportMessageAsync().Result;
                blobs.Add(sendThis);

                IGameGrain grain = GameGrainFactory.GetGrain(i);
                grains.Add(grain);
            }
        }
    }
}
