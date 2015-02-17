#define HEARTBEAT_TU1
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using Orleans;
using MidnightPresence.GrainInterfaces;
using ReachPresence.Utilities;
using Corinth.Blf.Reach.Presence;
using Corinth.Blf.Reach;
using LoadTestBase;

namespace PresenceConsoleTest
{
    public enum ActivityStatus : byte
    {
        None = 0,
        Matchmaking = 1,
        Pregame = 2,
        Playing = 3,
        Postgame = 4
    }

    public enum Activity : byte
    {
        None = 0xFF,//-1
        Activities = 0,
        Campaign = 1,
        Matchmaking = 2,
        Multiplayer = 3
    }

    public class PresenceWorker : OrleansClientWorkerBase
    {
        private static readonly short _startingHopperId = 101;
        private static readonly short _hopperBlockSize = 20;
        private static long _hopperSelectorId = -1;
        private static readonly bool RANDOMIZE_HEARBEATS = false;

        private static byte[][] heartbeats;
        private static IMidnightRequestRouter routerGrain;
        private int nUsers;
        private int _nStages;
        private int _nUsersPerStage;
        private long _nRequestsPerStage;
        private bool heartbeatFormatTU1;
        private int startPoint;
        private Random rand;

        public void ApplicationInitialize(int numUsers, int numStages, bool heartbeatFormatTU1, bool warmup = false)
        {
            if (numStages < 1)
            {
                numStages = 1;
            }

            this.nUsers = (numUsers > nRequests) ? (int)nRequests : numUsers;
            _nStages = numStages;
            _nUsersPerStage = nUsers / _nStages;
            _nRequestsPerStage = nRequests / _nStages;

            if (0 != nUsers % _nStages)
            {
                throw new ArgumentException(string.Format("the number of users ({0}) must be evenly divisible by the number of stages ({1}).", nUsers, _nStages));
            }
            if (0 != nRequests % _nStages)
            {
                throw new ArgumentException(string.Format("the number of requests ({0}) must be evenly divisible by the number of stages ({1}).", nRequests, _nStages));
            }

            this.rand = new Random();
            this.heartbeatFormatTU1 = heartbeatFormatTU1;
            this.startPoint = rand.Next(_nUsersPerStage);

            routerGrain = MidnightRequestRouterFactory.GetGrain(0); //rand.Next());

            //if (warmup)
            //    ActivateGrains(nUsers);

            WriteProgress("Generating heartbeat blobs...");
            heartbeats = new byte[nUsers][];
            for (int i = 0; i < nUsers; i++)
            {
                var id = i % nUsers;
                if (id == 0) id = 1;

                Corinth.Blf.Chunk hb;
                //if (HeartbeatFormatTU1)
                //{
                //    hb = CreateChunkPresenceHeartbeatTU1(id);
                //}
                //else
                //{
                //    hb = CreateChunkPresenceHeartbeat(id);
                //}

                hb = CreateMidnightChunkPresenceHeartbeat(id);
                heartbeats[i] = BlfHelper.SerializeChunk(hb);

                if (i % 10000 == 0)
                    WriteProgress(i.ToString());
            }
            WriteProgress("Done");
        }

        protected override Task IssueRequest(int requestNumber, int threadNumber)
        {
            long stageNum = requestNumber / _nRequestsPerStage;
            int indexBase = (int)(stageNum * _nUsersPerStage);
            int indexOffset;
            if (RANDOMIZE_HEARBEATS)
            {
                lock (rand)
                {
                    indexOffset = rand.Next(_nUsersPerStage);
                }
            }
            else
            {
                indexOffset = (requestNumber + startPoint) % _nUsersPerStage;
            }
            byte[] hb = heartbeats[indexBase + indexOffset];
            //var grain = GrainHelper.GetSessionGrain(hb.SessionID);
            //var grain = ReachRequestRouterFactory.GetGrain(0);//GrainHelper.GetRandomReachRequestRouter();

            //return grain.ProcessHeartbeat(hb, DateTime.UtcNow);
            //return grain.ProcessHeartbeat(BlfHelper.SerializeChunk(hb), DateTime.UtcNow);
            return routerGrain.Heartbeat(hb);//BlfHelper.SerializeChunk(hb));
        }

        private static ChunkPresenceHeartbeat CreateChunkPresenceHeartbeat(long id)
        {
            short hopperId = 0;

            var hb = new ChunkPresenceHeartbeat
            {
                MachineID = id,
                SessionID = id,
                IsHost = true,
                PlayerCount = 1,
                HostData = new HeartbeatHostData
                {
                    HopperID = hopperId,
                }
            };

            hb.PlayerData[0].PlayerXuid = id;
            hb.PlayerData[0].Appearance = new byte[ReachConstants.k_lsp_heartbeat_player_appearance_data_size];
            //hb.PlayerData[0].AppearanceSize = ReachConstants.k_lsp_heartbeat_player_appearance_data_size;

            hb.HostData.PlayerList[0].PlayerXuid = id;
            hb.HostData.PlayerList[0].PlayerTeam = 1;

            return hb;
        }

        private static ChunkPresenceHeartbeatTU1 CreateChunkPresenceHeartbeatTU1(long id)
        {
            short hopperId = (short)(_startingHopperId + (Interlocked.Increment(ref _hopperSelectorId) % _hopperBlockSize));

            var heartbeat = new ChunkPresenceHeartbeat
            {
                MachineID = id,
                SessionID = id,
                IsHost = true,
                PlayerCount = 1,
                HostData = new HeartbeatHostData
                {
                    HopperID = hopperId,
                    ActivityIsTeamGame = true,
                    ActivityMap = 1000,
                }
            };

            heartbeat.PlayerData[0].PlayerXuid = id;

            heartbeat.HostData.PlayerList[0].PlayerXuid = id;
            heartbeat.HostData.PlayerList[0].PlayerTeam = 1;

            return new ChunkPresenceHeartbeatTU1(heartbeat);
        }

        private static ChunkPresenceHeartbeat CreateMidnightChunkPresenceHeartbeat(long id)
        {
            HeartbeatPlayerData playerData = new HeartbeatPlayerData();
            HeartbeatHostData hostData = new HeartbeatHostData();
            hostData.CurrentPlayerCount = 1;
            playerData.PlayerXuid = id;

            hostData.PlayerList[0].PlayerXuid = id;

            // Set ourselves as Playing a Matchmaking game
            hostData.Activity = (byte)Activity.Multiplayer;
            hostData.ActivityStatus = (byte)ActivityStatus.Playing;

            var hb = new ChunkPresenceHeartbeat()
            {
                MachineID = id,
                SessionID = id,
                IsHost = true,
                //Number of local players
                PlayerCount = 1,
                HostData = hostData
            };
            hb.PlayerData[0] = playerData;

            return hb;
        }

        //private static void ActivateGrains(int numUsers)
        //{
        //    LoadTestDriverBase.WriteProgress("Starting warming up {0} users", numUsers);

        //    List<GrainReference> grains = new List<GrainReference>();
        //    const int CHUNK_SIZE = 5 * 1000;

        //    for (int chunk = 0; chunk < (numUsers / CHUNK_SIZE + 1); chunk++)
        //    {
        //        int lower = chunk * CHUNK_SIZE;
        //        int upper = (chunk + 1) * CHUNK_SIZE;
        //        if (upper > numUsers)
        //            upper = numUsers;

        //        grains.Clear();
        //        for (int i = lower; i < upper; i++)
        //        {
        //            grains.Add((GrainReference)GrainHelper.GetSessionGrain(i));
        //            grains.Add((GrainReference)GrainHelper.GetPlayerGrain(i));
        //        }

        //        LoadTestDriverBase.WriteProgress("Activating users {0} through {1} ... ", lower, upper);
        //        Domain.Current.ActivateGrains(grains).Wait();
        //        LoadTestDriverBase.WriteProgress("Done");
        //    }
        //}
    }
}