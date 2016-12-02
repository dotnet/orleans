using System;
using Orleans;
using Orleans.EventSourcing;
using TestGrainInterfaces;

namespace TestGrains
{
    [Serializable]
    public class PersonState
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public GenderType Gender { get; set; }
        public bool IsMarried { get; set; }

        public void Apply(PersonRegistered @event)
        {
            this.FirstName = @event.FirstName;
            this.LastName = @event.LastName;
            this.Gender = @event.Gender;
        }

        public void Apply(PersonMarried @event)
        {
            this.IsMarried = true;
        }

        public void Apply(PersonLastNameChanged @event)
        {
            this.LastName = @event.LastName;
        }
    }
}
