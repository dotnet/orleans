using System;
using System.Threading.Tasks;

namespace TestGrainInterfaces
{
    public enum GenderType
    {
        Male,
        Female
    }

    [Serializable]
    public class PersonAttributes
    {
        public string FirstName { get; set; }
        public string LastName { get; set; }
        public GenderType Gender { get; set; }
    }

    /// <summary>
    /// Orleans grain communication interface IPerson
    /// </summary>
    public interface IPersonGrain : Orleans.IGrainWithGuidKey
    {
        Task RegisterBirth(PersonAttributes person);
        Task Marry(IPersonGrain spouse);

        Task<PersonAttributes> GetTentativePersonalAttributes();

        // Tests

        Task RunTentativeConfirmedStateTest();
    }
}
