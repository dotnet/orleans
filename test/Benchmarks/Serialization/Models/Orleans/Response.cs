using System;

namespace FakeFx.Runtime
{
    [Serializable]
    [Orleans.GenerateSerializer]
    [Orleans.WellKnownId(103)]
    [Orleans.SuppressReferenceTracking]
    internal sealed class Response
    {
        [Orleans.Id(1)]
        public bool ExceptionFlag { get; private set; }

        [Orleans.Id(2)]
        public Exception Exception { get; private set; }

        [Orleans.Id(3)]
        public object Data { get; private set; }

        public Response(object data)
        {
            switch (data)
            {
                case Exception exception:
                    Exception = exception;
                    ExceptionFlag = true;
                    break;
                default:
                    Data = data;
                    ExceptionFlag = false;
                    break;
            }
        }

        private Response()
        {
        }

        static public Response ExceptionResponse(Exception exc)
        {
            return new()
            {
                ExceptionFlag = true,
                Exception = exc
            };
        }

        public override string ToString()
        {
            if (ExceptionFlag)
            {
                return $"Response Exception={Exception}";
            }

            return $"Response Data={Data}";
        }
    }
}
