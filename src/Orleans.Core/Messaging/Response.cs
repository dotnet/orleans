using System;

namespace Orleans.Runtime
{
    [Serializable]
    internal class Response
    {
        public bool ExceptionFlag { get; private set; }
        public Exception Exception { get; private set; }
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
            return new Response
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
