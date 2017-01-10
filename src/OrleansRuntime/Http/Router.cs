using Microsoft.Owin;
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

namespace Orleans.Runtime
{

    /// <summary>
    /// A simple HTTP router which matches registered routes against the paths of incoming requests
    /// </summary>
    internal class Router
    {

        readonly char[] pathSplitChars = new char[] { '/' };
        const string variableStartsWith = ":";

        public Router()
        {
            routes = new Dictionary<string, Func<IOwinContext, IDictionary<string, string>, Task>>();
        }

        IDictionary<string, Func<IOwinContext, IDictionary<string, string>, Task>> routes;

        public void Add(string pattern, Func<IOwinContext, IDictionary<string, string>, Task> func)
        {
            routes.Add(pattern, func);
        }

        public Func<IOwinContext, Task> Match(string path)
        {
            foreach (var route in routes)
            {
                var result = IsMatch(path, route.Key);
                if (null == result) continue;
                
                // the path matches a registerd route
                return new Func<IOwinContext, Task>(x => route.Value(x, result));
            }
            return null;
        }

        // i.e matches /foo/bar/baz with /foo/:bar/:baz
        IDictionary<string, string> IsMatch(string path, string route)
        {
            var pathParts = path.Split(pathSplitChars);
            var routeParts = route.Split(pathSplitChars);

            if (pathParts.Length != routeParts.Length) return null;

            var dictionary = new Dictionary<string, string>();
            for (var i = 0; i < pathParts.Length; i++)
            {
                var routePart = routeParts[i];
                var pathPart = pathParts[i];
                if (routePart.StartsWith(variableStartsWith))
                {
                    dictionary.Add(routePart.Substring(1), pathPart);
                    continue;
                }
                if (routePart != pathPart)
                {
                    return null;
                }
            }
            return dictionary;
        }


    }
}
