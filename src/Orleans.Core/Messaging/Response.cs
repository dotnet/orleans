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
            Exception = data as Exception;
            if (Exception == null)
            {
                Data = data;
                ExceptionFlag = false;
            }
            else
            {
                Data = null;
                ExceptionFlag = true;
            }
        }

        static public Response ExceptionResponse(Exception exc)
        {
            return new Response(null) {ExceptionFlag = true, Exception = exc, Data = null};
        }

        public override string ToString()
        {
            return String.Format("Response ExceptionFlag={0}", ExceptionFlag);
        }

        private static readonly Response done = new Response(null);
        public static Response Done { get { return done; } }
    }
}
