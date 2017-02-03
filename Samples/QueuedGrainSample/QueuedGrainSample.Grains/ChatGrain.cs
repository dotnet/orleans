﻿using System;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using ReplicatedChatGrainSample.Interfaces;
using Orleans;
using Orleans.Providers;
using Orleans.Concurrency;
using Orleans.EventSourcing;

namespace ReplicatedChatGrainSample.Grains
{
    [Serializable]
    public class ChatState
    {
        public List<string> Messages { get; set; }

        public ChatState()
        {
            Messages = new List<string>();
        }

        public void Apply(AppendMessageEvent e)
        {
            Messages.Add(e.Message);
        }

        public void Apply(ClearAllMessagesEvent e)
        {
            Messages.Clear();
        }
    }

 
    [Serializable]
    public class AppendMessageEvent
    {
        public string Message { get; set; }

        public override string ToString()
        {
            return string.Format("[AppendMessage \"{0}\"]", Message);
        }
    }

    [Serializable]
    public class ClearAllMessagesEvent
    {   
        public override string ToString()
        {
            return string.Format("[ClearAllMessages]");
        }
    }


    [StorageProvider(ProviderName = "GloballySharedAzureAccount")]
    public class ChatGrain : JournaledGrain<ChatState>, IChatGrainInterface
    {
        public Task<LocalState> AppendMessage(string msg)
        {
            RaiseEvent(new AppendMessageEvent() { Message = msg });
            return GetLocalState();
        }

        public Task<LocalState> ClearAll()
        {
            RaiseEvent(new ClearAllMessagesEvent());
            return GetLocalState();
        }

        public Task<LocalState> GetLocalState()
        {
            return Task.FromResult(new LocalState()
            {
                TentativeState = this.TentativeState.Messages,
                ConfirmedState = this.State.Messages,
                UnconfirmedEvents = this.UnconfirmedEvents.Select((u) => u.ToString()).ToList()
            });
        }

    }
    
}
