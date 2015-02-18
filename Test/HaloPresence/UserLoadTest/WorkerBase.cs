#define HEARTBEAT_TU1
using System;
using System.Collections.Generic;
using System.Net;
using System.Threading.Tasks;
using Corinth.Blf.Reach;
using Corinth.Blf.Reach.Presence;
using MidnightPresence.GrainInterfaces;
using Orleans;
using Orleans.Runtime.Configuration;
using ReachPresence.Utilities;
using System.Threading;
using LoadTestBase;
using Orleans.Runtime.Host;

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

    abstract class WorkerBase : MarshalByRefObject
    {
        internal bool HeartbeatFormatTU1 { get; set; }

        private static readonly short _startingHopperId = 101;
        private static readonly short _hopperBlockSize = 20;
        private static long _hopperSelectorId = -1;

        protected int nUsers;
        protected long nRequests;
        protected int _reportBlockSize;
        protected int _pipelineSize;
        protected Callback _callback;
        protected string name;
        protected Random rand;
        private static byte[][] heartbeats;
        private static IMidnightRequestRouter routerGrain;

        public virtual void Initialize(int numUsers, long numRequests, int reportBlockSize, int pipelineSize, Callback callback, IPEndPoint gateway, int instanceIndex, bool useAzureSiloTable, bool warmup = false)
        {
            nUsers = (numUsers > numRequests) ? (int)numRequests : numUsers;
            nRequests = numRequests;
            _reportBlockSize = reportBlockSize;
            _pipelineSize = pipelineSize;
            _callback = callback;
            name = AppDomain.CurrentDomain.FriendlyName;
            rand = new Random();
            if (!Orleans.GrainClient.IsInitialized)
            {
                ClientConfiguration config = ClientConfiguration.StandardLoad();

                if (useAzureSiloTable)
                {
                    AzureClient.Initialize(config);
                }
                else 
                {
                    if (instanceIndex >= 0)
                    {
                        // Use specified silo index from config file for GW selection.
                        config.PreferedGatewayIndex = instanceIndex % config.Gateways.Count;
                    }
                    else if (gateway != null)
                    {
                        // Use specified gateway address passed on command line
                        if (!config.Gateways.Contains(gateway))
                        {
                            config.Gateways.Add(gateway);
                        }
                        config.PreferedGatewayIndex = config.Gateways.IndexOf(gateway);
                    }
                    // Else just use standard config from file

                    Orleans.GrainClient.Initialize(config);
                }
            }

            routerGrain = MidnightRequestRouterFactory.GetGrain(0); //rand.Next());

            //if(warmup)
            //    ActivateGrains();

            Console.WriteLine("Generating heartbeat blobs...");
            heartbeats = new byte[nUsers][];
            for (int i=0; i<nUsers; i++)
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
                
                if( i % 10000 == 0)
                    Console.WriteLine(i);
            }
            Console.WriteLine("Done");
        }

        public abstract List<Exception> Run();
        
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

            var hb =  new ChunkPresenceHeartbeat()
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

        protected static Task RunIteration(int i, int nUsers)
        {
            byte[] hb = heartbeats[i % nUsers];

            //var grain = GrainHelper.GetSessionGrain(hb.SessionID);
            //var grain = ReachRequestRouterFactory.GetGrain(0);//GrainHelper.GetRandomReachRequestRouter();

            //return grain.ProcessHeartbeat(hb, DateTime.UtcNow);
            //return grain.ProcessHeartbeat(BlfHelper.SerializeChunk(hb), DateTime.UtcNow);
            return routerGrain.Heartbeat(hb);//BlfHelper.SerializeChunk(hb));
        }

        //public void ActivateGrains()
        //{
        //    Console.WriteLine("Starting warming up {0} users", nUsers);

        //    List<GrainReference> grains = new List<GrainReference>();
        //    const int CHUNK_SIZE = 5*1000;

        //    for (int chunk = 0; chunk < (nUsers / CHUNK_SIZE + 1); chunk++)
        //    {
        //        int lower = chunk*CHUNK_SIZE;
        //        int upper = (chunk + 1)*CHUNK_SIZE;
        //        if (upper > nUsers)
        //            upper = nUsers;

        //        grains.Clear();
        //        for (int i = lower; i < upper; i++)
        //        {
        //            grains.Add((GrainReference) GrainHelper.GetSessionGrain(i));
        //            grains.Add((GrainReference) GrainHelper.GetPlayerGrain(i));
        //        }

        //        Console.Write("Activating users {0} through {1} ... ", lower, upper);
        //        Domain.Current.ActivateGrains(grains).Wait();
        //        Console.WriteLine("Done");
        //    }
        //}
    }
}