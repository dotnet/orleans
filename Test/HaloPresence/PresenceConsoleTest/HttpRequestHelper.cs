using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Linq;
using System.Text;
using System.Net;
using System.IO;
using System.Runtime.Serialization;
using Bungie.Blf;
using Bungie.Blf.Serialization;
using ReachPresence.Utilities;

namespace WebServicesTest.Helpers
{
    public static class HttpRequestHelper
    {
        private const string BoundaryName = "BUNGIEr0x0rz";
        private const string MimeItemStartFormat = "--" + BoundaryName + "\r\n" +
                                     "Content-Disposition: file; name=\"upload\"; filename=\"upload.blf\"\r\n" +
                                     "Content-Type: {0}\r\n\r\n";
        private const string MimeItemEnd = "\r\n--" + BoundaryName + "--\r\n";

        public static string ConstructQueryString(string querystringTemplate, NameValueCollection additionalParams, params string[] args)
        {
            return String.Format(querystringTemplate, args) + GetAdditionalQuerystringParams(additionalParams);
        }

        public static string GetAdditionalQuerystringParams(NameValueCollection additionalParams)
        {
            string querystring = String.Empty;

            if (additionalParams != null && additionalParams.Count > 0)
            {
                foreach (string param in additionalParams)
                {
                    querystring = String.Format("{0}&{1}={2}", querystring, param, additionalParams[param]);
                }
            }

            return querystring;
        }

        public static HttpWebRequest CreateWebRequest(RequestMethod method, string url, string querystring)
        {
            return CreateWebRequest(method, url, querystring, "", null);
        }

        public static HttpWebRequest CreateWebRequest(RequestMethod method, string url, string querystring, BlfFile blfContent)
        {
            return CreateWebRequest(method, url, querystring, blfContent, "application");
        }

        public static HttpWebRequest CreateWebRequest(RequestMethod method, string url, string querystring, BlfFile blfContent, string innerContentType)
        {
            string outerContentType = String.Format("multipart/form-data; boundary={0}", BoundaryName);
            byte[] contentBytes = null;

            // Add the contents
            if (blfContent != null)
            {
                using (MemoryStream memStr = new MemoryStream())
                {
                    string mimeItemStart = String.Format(MimeItemStartFormat, innerContentType);
                    memStr.Write(Encoding.ASCII.GetBytes(mimeItemStart), 0, mimeItemStart.Length);
                    memStr.SerializeBlfFile(blfContent);
                    memStr.Write(Encoding.ASCII.GetBytes(MimeItemEnd), 0, MimeItemEnd.Length);
                    contentBytes = memStr.ToArray();
                }
            }

            return CreateWebRequest(method, url, querystring, outerContentType, contentBytes);
        }

        public static HttpWebRequest CreateWebRequest(RequestMethod method, string url, string querystring, Chunk chunkContent)
        {
            return CreateWebRequest(method, url, querystring, chunkContent, "application");
        }

        public static HttpWebRequest CreateWebRequest(RequestMethod method, string url, string querystring, Chunk chunkContent, string innerContentType)
        {
            string outerContentType = String.Format("multipart/form-data; boundary={0}", BoundaryName);
            byte[] contentBytes = null;

            // Add the contents
            if (chunkContent != null)
            {
                using (MemoryStream memStr = new MemoryStream())
                {
                    string mimeItemStart = String.Format(MimeItemStartFormat, innerContentType);
                    memStr.Write(Encoding.ASCII.GetBytes(mimeItemStart), 0, mimeItemStart.Length);
                    memStr.SerializeChunk(chunkContent);
                    memStr.Write(Encoding.ASCII.GetBytes(MimeItemEnd), 0, MimeItemEnd.Length);
                    contentBytes = memStr.ToArray();
                }
            }

            return CreateWebRequest(method, url, querystring, outerContentType, contentBytes);
        }

        public static HttpWebRequest CreateWebRequest(RequestMethod method, string url, string querystring, string outerContentType, byte[] content)
        {
            // Construct the Uri
            UriBuilder requestUri = new UriBuilder(url);
            if (!String.IsNullOrEmpty(querystring))
            {
                requestUri.Query = querystring;
            }

            // Create the web request
            HttpWebRequest request = HttpWebRequest.Create(requestUri.Uri) as HttpWebRequest;
            request.Method = method.ToString();
            request.Timeout = (1000 * 60 * 2);        // 2 minute timeout
            request.KeepAlive = true;

            //Add the content
            if (content != null && content.Length > 0)
            {
                request.ContentType = outerContentType;
                request.ContentLength = content.Length;
                using (Stream bodyStream = request.GetRequestStream())
                {
                    bodyStream.Write(content, 0, content.Length);
                }
            }

            return request;
        }
    }

    public enum RequestMethod
    {
        DELETE,
        HEAD,
        GET,
        POST,
        PUT
    }
}
