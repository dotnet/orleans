using System;
using System.Threading.Tasks;
using Orleans;
using Orleans.EventSourcing;
using TestGrainInterfaces;

namespace TestGrains
{
    public class PersonRegistered
    {
        public string FirstName { get; private set; }
        public string LastName { get; private set; }
        public GenderType Gender { get; private set; }

        public PersonRegistered(string firstName, string lastName, GenderType gender)
        {
            FirstName = firstName;
            LastName = lastName;
            Gender = gender;
        }
    }

    public class PersonMarried
    {
        public Guid SpouseId { get; private set; }
        public string SpouseFirstName { get; private set; }
        public string SpouseLastName { get; private set; }
        
        public PersonMarried(Guid spouseId, string spouseFirstName, string spouseLastName)
        {
            SpouseId = spouseId;
            SpouseFirstName = spouseFirstName;
            SpouseLastName = spouseLastName;
        }
    }

    public class PersonLastNameChanged
    {
        public string LastName { get; private set; }

        public PersonLastNameChanged(string lastName)
        {
            LastName = lastName;
        }
    }
}
