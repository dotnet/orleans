using System;
using System.Net;

namespace Orleans.Storage
{
    /// <summary>
    /// Interface to be optionally implemented by storage providers to return richer exception details.
    /// </summary>
    public interface IRestExceptionDecoder
    {
        /// <summary>
        /// Decode details of the exceprion
        /// </summary>
        /// <param name="e">Excption to decode</param>
        /// <param name="httpStatusCode">HTTP status code for the error</param>
        /// <param name="restStatus">REST status for the error</param>
        /// <param name="getExtendedErrors">Whether or not to extract REST error code</param>
        /// <returns></returns>
        bool DecodeException(Exception e, out HttpStatusCode httpStatusCode, out string restStatus, bool getExtendedErrors = false);
    }
}