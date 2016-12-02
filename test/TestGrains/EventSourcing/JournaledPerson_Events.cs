using System;
using TestGrainInterfaces;

namespace TestGrains
{
    // we use a marker interface, so we get a bit more typechecking than with plain objects
    public interface IPersonEvent { } 


    public class PersonRegistered : IPersonEvent
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

    public class PersonMarried : IPersonEvent
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

    public class PersonLastNameChanged : IPersonEvent
    {
        public string LastName { get; private set; }

        public PersonLastNameChanged(string lastName)
        {
            LastName = lastName;
        }
    }
}
