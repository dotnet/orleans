using System;
using TestGrainInterfaces;

namespace TestGrains
{
    // we use a marker interface, so we get a bit more typechecking than with plain objects
    public interface IPersonEvent { } 

    [Serializable]
    public class PersonRegistered : IPersonEvent
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public GenderType Gender { get; set; }

        public PersonRegistered(string firstName, string lastName, GenderType gender)
        {
            FirstName = firstName;
            LastName = lastName;
            Gender = gender;
        }
    }

    [Serializable]
    public class PersonMarried : IPersonEvent
    {
        public Guid SpouseId { get; set; }
        public string SpouseFirstName { get; set; }
        public string SpouseLastName { get; set; }
        
        public PersonMarried(Guid spouseId, string spouseFirstName, string spouseLastName)
        {
            SpouseId = spouseId;
            SpouseFirstName = spouseFirstName;
            SpouseLastName = spouseLastName;
        }
    }

    [Serializable]
    public class PersonLastNameChanged : IPersonEvent
    {
        public string LastName { get; set; }

        public PersonLastNameChanged(string lastName)
        {
            LastName = lastName;
        }
    }
}
