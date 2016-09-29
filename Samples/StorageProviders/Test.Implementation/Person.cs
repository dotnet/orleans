using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Text;
using Orleans;
using Orleans.Providers;
using Test.Interfaces;

namespace Test.Implementation
{
    [StorageProvider(ProviderName = "TestStore")]
    public class Person : Grain<PersonState>, Test.Interfaces.IPerson
    {
        Task IPerson.SetPersonalAttributes(PersonalAttributes props)
        {
            State.FirstName = props.FirstName;
            State.LastName = props.LastName;
            State.Gender = props.Gender;
            return WriteStateAsync();
        }

        Task<string> IPerson.GetFirstName()
        {
            return Task.FromResult(State.FirstName);
        }

        Task<string> IPerson.GetLastName()
        {
            return Task.FromResult(State.LastName);
        }

        Task<GenderType> IPerson.GetGender()
        {
            return Task.FromResult(State.Gender);
        }
    }

    public class PersonState
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public GenderType Gender { get; set; }
    }
}
