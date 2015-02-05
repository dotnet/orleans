using Orleans.TestFramework;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace LoadTest
{
    public class ElasticityConfigHelpers
    {
        private static int GetParamPos(ClientOptions c, string paramName)
        {
            for (int i = 0; i < c.AdditionalParameters.Length; i++)
            {
                if (c.AdditionalParameters[i] == paramName && (i + 1) < c.AdditionalParameters.Length)
                {
                    return i + 1;
                }
            }

            return -1;
        }

        private static string MakeExcelFileName(string prefix, ClientOptions c)
        {
            string grains = "UNSET";
            int grainPos = GetParamPos(c, "-grains");
            if (grainPos >= 0)
            {
                grains = c.AdditionalParameters[grainPos];
            }

            string functionType = "UNSET";
            int functionPos = GetParamPos(c, "-functionType");
            if (functionPos >= 0)
            {
                functionType = c.AdditionalParameters[functionPos];
            }

            return string.Format("{0}-{1}-S{2}-C{3}-SPC{4}-G{5}-N{6}",
                prefix, functionType, c.ServerCount, c.ClientCount, 
                c.ServersPerClient, grains, c.Number);
        }

        public static ClientOptions MakeOptions(string testName, ClientOptions template, Action<ClientOptions> adaptOptions = null)
        {
            ClientOptions newOptions = template.Copy();
            if (adaptOptions != null) 
            { 
                adaptOptions(newOptions);
            }

            // We want to have an accurate count of grains just for the file and logs in case we create
            // new grains per request (the -grains parameter value is not essential for NewGrainPerRequest
            // as amount of grains created depends on clients and number of requests).
            var parameters = newOptions.AdditionalParameters.ToList();
            int functionPos = GetParamPos(newOptions, "-functionType");
            if (functionPos >= 0 && newOptions.AdditionalParameters[functionPos] == "NewGrainPerRequest")
            {
                string grainCount = (newOptions.ClientCount * newOptions.Number).ToString();

                int grainPos = GetParamPos(newOptions, "-grains");
                if (grainPos >= 0)
                {
                    // Parameter already exists in array
                    newOptions.AdditionalParameters[grainPos] = (newOptions.ClientCount * newOptions.Number).ToString();
                }
                else
                {
                    // Add it as a new parameter
                    parameters.Add("-grains");
                    parameters.Add(grainCount);
                }
            }
            newOptions.AdditionalParameters = parameters.ToArray();

            parameters = newOptions.AdditionalParameters.ToList();
            parameters.Add("-excelName");
            parameters.Add(MakeExcelFileName(testName, newOptions));
            newOptions.AdditionalParameters = parameters.ToArray();

            return newOptions;
        }
    }
}
