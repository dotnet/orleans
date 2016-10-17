namespace Orleans.Serialization
{
    using System;

    using Orleans.Runtime;

    [Serializable]
    public class IlCodeGenerationException : OrleansException
    {
        public IlCodeGenerationException()
        {
        }

        public IlCodeGenerationException(string message)
            : base(message)
        {
        }

        public IlCodeGenerationException(string message, Exception innerException)
            : base(message, innerException)
        {
        }
    }
}