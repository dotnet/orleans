namespace TestGrainInterfaces
{
    public enum GenderType
    {
        Male,
        Female
    }

    [Serializable]
    [Orleans.GenerateSerializer]
    public class PersonAttributes
    {
        [Orleans.Id(0)]
        public string FirstName { get; set; }
        [Orleans.Id(1)]
        public string LastName { get; set; }
        [Orleans.Id(2)]
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
