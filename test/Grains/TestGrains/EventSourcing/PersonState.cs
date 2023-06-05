using TestGrainInterfaces;

namespace TestGrains
{
    [Serializable]
    [Orleans.GenerateSerializer]
    public class PersonState
    {
        [Orleans.Id(0)]
        public string FirstName { get; set; }
        [Orleans.Id(1)]
        public string LastName { get; set; }
        [Orleans.Id(2)]
        public GenderType Gender { get; set; }
        [Orleans.Id(3)]
        public bool IsMarried { get; set; }

        public void Apply(PersonRegistered @event)
        {
            FirstName = @event.FirstName;
            LastName = @event.LastName;
            Gender = @event.Gender;
        }

        public void Apply(PersonMarried @event) => IsMarried = true;

        public void Apply(PersonLastNameChanged @event) => LastName = @event.LastName;
    }
}
