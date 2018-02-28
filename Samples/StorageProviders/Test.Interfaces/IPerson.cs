using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Orleans;

namespace Test.Interfaces
{
    public enum GenderType {  Male, Female }

    [Serializable]
    public class PersonalAttributes
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public GenderType Gender { get; set; }
    }

    /// <summary>
    /// Orleans grain communication interface IPerson
    /// </summary>
    public interface IPerson : IGrainWithIntegerKey
    {
        Task SetPersonalAttributes(PersonalAttributes person);

        Task<string> GetFirstName();
        Task<string> GetLastName();
        Task<GenderType> GetGender();
    }
}
