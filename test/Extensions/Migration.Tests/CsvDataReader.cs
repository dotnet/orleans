using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using Xunit.Sdk;

namespace Migration.Tests
{
    public class CsvDataReader : DataAttribute
    {
        private readonly string _filename;

        public CsvDataReader(string filename)
        {
            this._filename = filename;
        }

        public override IEnumerable<object[]> GetData(MethodInfo testMethod)
        {
            var parameters = testMethod.GetParameters();
            using var reader = new StreamReader(_filename);
            string? line = null;
            var lineNumber = 1;
            while ((line = reader.ReadLine()) != null)
            {
                if (line.StartsWith('#'))
                {
                    continue;
                }
                var rows = line.Split(';');
                if (rows.Length != parameters.Length)
                {
                    throw new ArgumentException($"Invalid rows at line {lineNumber}");
                }
                var results = new List<object>(parameters.Length);
                var n = 0;
                foreach (var p in parameters)
                {
                    switch ((Type.GetTypeCode(p.ParameterType)))
                    {
                        case TypeCode.Boolean:
                            results.Add(bool.Parse(rows[n]));
                            break;
                        case TypeCode.Int32:
                            results.Add(int.Parse(rows[n]));
                            break;
                        case TypeCode.String:
                            results.Add(rows[n].Trim('\"'));
                            break;
                        case TypeCode.Object:
                            if (!TryHandleObject(lineNumber, p, rows[n], out var obj))
                            {
                                ThrowArgumentException(lineNumber, p);
                            }
                            results.Add(obj);
                            break;
                        default:
                            ThrowArgumentException(lineNumber, p);
                            break;
                    }
                    n++;
                }
                lineNumber++;
                yield return results.ToArray();
            }
        }

        private static bool TryHandleObject(int lineNumber, ParameterInfo parameter, string value, out object result)
        {
            try
            {
                if (parameter.ParameterType == typeof(Type))
                {
                    foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        var type = assembly.GetType(value);
                        if (type != null)
                        {
                            if (type.IsGenericType && !type.IsConstructedGenericType)
                            {
                                var typeArguments = new List<Type>();
                                foreach (var typeArgument in type.GetGenericArguments())
                                {
                                    // TODO be more clever?
                                    //typeArguments.Add(typeArgument.GetType());
                                    typeArguments.Add(typeof(object));
                                }
                                result = type.MakeGenericType(typeArguments.ToArray());
                            }
                            else
                            {
                                result = type;
                            }
                            return true;
                        }
                    }
                }
                throw new Exception($"Type {value} not found");
            }
            catch (Exception ex)
            {
                ThrowArgumentException(lineNumber, parameter, ex);
            }
            result = new object();
            return false;
        }

        private static void ThrowArgumentException(int lineNumber, ParameterInfo p, Exception? inner = null) => throw new ArgumentException($"Line {lineNumber}: Type {p.ParameterType} not supported for parameter \"{p.Name}\"", inner);
    }
}
