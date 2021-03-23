using Orleans.Streams;

namespace UnitTests.GrainInterfaces
{
    public class RedStreamNamespacePredicate : IStreamNamespacePredicate
    {
        public string PredicatePattern => ConstructorStreamNamespacePredicateProvider.FormatPattern(typeof(RedStreamNamespacePredicate), constructorArgument: null);

        public bool IsMatch(string streamNamespace)
        {
            return streamNamespace.StartsWith("red");
        }
    }
}