/*
Project Orleans Cloud Service SDK ver. 1.0
 
Copyright (c) Microsoft Corporation
 
All rights reserved.
 
MIT License

Permission is hereby granted, free of charge, to any person obtaining a copy of this software and 
associated documentation files (the ""Software""), to deal in the Software without restriction,
including without limitation the rights to use, copy, modify, merge, publish, distribute, sublicense,
and/or sell copies of the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED *AS IS*, WITHOUT WARRANTY OF ANY KIND, EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO
THE WARRANTIES OF MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS
OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

using System;
using System.Globalization;

namespace Orleans.SqlUtils
{
    /// <summary>
    /// Formats .NET types appropriately for database consumption in non-parameterized queries.
    /// </summary>
    public class SqlFormatProvider: IFormatProvider
    {
        private readonly SqlFormatter formatter = new SqlFormatter();

        /// <summary>
        /// Returns an instance of the formatter
        /// </summary>
        /// <param name="formatType">Requested format type</param>
        /// <returns></returns>
        public object GetFormat(Type formatType)
        {
            return formatType == typeof(ICustomFormatter) ? formatter : null;
        }


        private class SqlFormatter: ICustomFormatter
        {
            public string Format(string format, object arg, IFormatProvider formatProvider)
            {
                //This null check applies also to Nullable<T> when T does not have value defined.
                if(arg == null)
                {
                    return "NULL";
                }

                if(arg is string)
                {
                    return "N'" + ((string)arg).Replace("'", "''") + "'";
                }

                if(arg is DateTime)
                {
                    return "'" + ((DateTime)arg).ToString("O") + "'";
                }

                if(arg is DateTimeOffset)
                {
                    return "'" + ((DateTimeOffset)arg).ToString("O") + "'";
                }

                if(arg is IFormattable)
                {
                    return ((IFormattable)arg).ToString(format, CultureInfo.InvariantCulture);
                }

                return arg.ToString();
            }
        }
    }
}
