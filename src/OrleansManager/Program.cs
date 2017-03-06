using System;
using System.Collections.Generic;
using System.Linq;
using Orleans;
using Orleans.Runtime;

namespace OrleansManager
{
    class Program
    {
        private static IManagementGrain systemManagement;

        static void Main(string[] args)
        {
            Console.WriteLine("Invoked OrleansManager.exe with arguments {0}", Utils.EnumerableToString(args));

            var command = args.Length > 0 ? args[0].ToLowerInvariant() : "";

            if (string.IsNullOrEmpty(command) || command.Equals("/?") || command.Equals("-?"))
            {
                PrintUsage();
                Environment.Exit(-1);
            }

            try
            {
                RunCommand(command, args);
                Environment.Exit(0);
            }
            catch (Exception exc)
            {
                Console.WriteLine("Terminating due to exception:");
                Console.WriteLine(exc.ToString());
            }
        }

        private static void RunCommand(string command, string[] args)
        {
            GrainClient.Initialize();

            systemManagement = GrainClient.GrainFactory.GetGrain<IManagementGrain>(0);
            Dictionary<string, string> options = args.Skip(1)
                .Where(s => s.StartsWith("-"))
                .Select(s => s.Substring(1).Split('='))
                .ToDictionary(a => a[0].ToLowerInvariant(), a => a.Length > 1 ? a[1] : "");

            var restWithoutOptions = args.Skip(1).Where(s => !s.StartsWith("-")).ToArray();

            switch (command)
            {
                case "grainstats":
                    PrintSimpleGrainStatistics(restWithoutOptions);
                    break;

                case "fullgrainstats":
                    PrintGrainStatistics(restWithoutOptions);
                    break;

                case "collect":
                    CollectActivations(options, restWithoutOptions);
                    break;

                case "unregister":
                    var unregisterArgs = args.Skip(1).ToArray();
                    UnregisterGrain(unregisterArgs);
                    break;

                case "lookup":
                    var lookupArgs = args.Skip(1).ToArray();
                    LookupGrain(lookupArgs);
                    break;

                case "grainreport":
                    var grainReportArgs = args.Skip(1).ToArray();
                    GrainReport(grainReportArgs);
                    break;

                default:
                    PrintUsage();
                    break;
            }
        }

        private static void PrintUsage()
        {
            Console.WriteLine(@"Usage:
    OrleansManager grainstats [silo1 silo2 ...]
    OrleansManager fullgrainstats [silo1 silo2 ...]
    OrleansManager collect [-memory=nnn] [-age=nnn] [silo1 silo2 ...]
    OrleansManager unregister <grain interface type code (int)|grain implementation class name (string)> <grain id long|grain id Guid>
    OrleansManager lookup <grain interface type code (int)|grain implementation class name (string)> <grain id long|grain id Guid>
    OrleansManager grainReport <grain interface type code (int)|grain implementation class name (string)> <grain id long|grain id Guid>");
        }

        private static void CollectActivations(IReadOnlyDictionary<string, string> options, IEnumerable<string> args)
        {
            var silos = args.Select(ParseSilo).ToArray();
            int ageLimitSeconds = 0;
            string s;

            if (options.TryGetValue("age", out s))
                int.TryParse(s, out ageLimitSeconds);

            var ageLimit = TimeSpan.FromSeconds(ageLimitSeconds);
            if (ageLimit > TimeSpan.Zero)
                systemManagement.ForceActivationCollection(silos, ageLimit);
            else
                systemManagement.ForceGarbageCollection(silos);
        }

        private static void PrintSimpleGrainStatistics(IEnumerable<string> args)
        {
            var silos = args.Select(ParseSilo).ToArray();
            var stats = systemManagement.GetSimpleGrainStatistics(silos).Result;
            Console.WriteLine("Silo                   Activations  Type");
            Console.WriteLine("---------------------  -----------  ------------");
            foreach (var s in stats.OrderBy(s => s.SiloAddress + s.GrainType))
                Console.WriteLine("{0}  {1}  {2}", s.SiloAddress.ToString().PadRight(21), Pad(s.ActivationCount, 11), s.GrainType);
        }
        
        private static void PrintGrainStatistics(IEnumerable<string> args)
        {
            var silos = args.Select(ParseSilo).ToArray();
            var stats = systemManagement.GetSimpleGrainStatistics(silos).Result;
            Console.WriteLine("Act  Type");
            Console.WriteLine("--------  -----  ------  ------------");
            foreach (var s in stats.OrderBy(s => Tuple.Create(s.GrainType, s.ActivationCount)))
                Console.WriteLine("{0}  {1}", Pad(s.ActivationCount, 8), s.GrainType);
        }

        private static void GrainReport(string[] args)
        {
            var grainId = ConstructGrainId(args, "GrainReport");

            var silos = GetSiloAddresses();
            if (silos == null || silos.Count == 0) return;

            var reports = new List<DetailedGrainReport>();
            foreach (var silo in silos)
            {
                WriteStatus(string.Format("**Calling GetDetailedGrainReport({0}, {1})", silo, grainId));
                try
                {
                    var siloControl = GrainClient.InternalGrainFactory.GetSystemTarget<ISiloControl>(Constants.SiloControlId, silo);
                    DetailedGrainReport grainReport = siloControl.GetDetailedGrainReport(grainId).Result;
                    reports.Add(grainReport);
                }
                catch (Exception exc)
                {
                    WriteStatus(string.Format("**Failed to get grain report from silo {0}. Exc: {1}", silo, exc.ToString()));
                }
            }
            foreach (var grainReport in reports)
                WriteStatus(grainReport.ToString());
            
            LookupGrain(args);
        }

        private static void UnregisterGrain(string[] args)
        {
            var grainId = ConstructGrainId(args, "unregister");

            var silo = GetSiloAddress();
            if (silo == null) return;

            var directory = GrainClient.InternalGrainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceId, silo);

            WriteStatus(string.Format("**Calling DeleteGrain({0}, {1})", silo, grainId));
            directory.DeleteGrainAsync(grainId).Wait();
            WriteStatus(string.Format("**DeleteGrain finished OK."));
        }

        private static async void LookupGrain(string[] args)
        {
            var grainId = ConstructGrainId(args, "lookup");

            var silo = GetSiloAddress();
            if (silo == null) return;

            var directory = GrainClient.InternalGrainFactory.GetSystemTarget<IRemoteGrainDirectory>(Constants.DirectoryServiceId, silo);
  
            WriteStatus(string.Format("**Calling LookupGrain({0}, {1})", silo, grainId));
            //Tuple<List<Tuple<SiloAddress, ActivationId>>, int> lookupResult = await directory.FullLookUp(grainId, true);
            var lookupResult = await directory.LookupAsync(grainId);

            WriteStatus(string.Format("**LookupGrain finished OK. Lookup result is:"));
            var list = lookupResult.Addresses;
            if (list == null)
            {
                WriteStatus(string.Format("**The returned activation list is null."));
                return;
            }
            if (list.Count == 0)
            {
                WriteStatus(string.Format("**The returned activation list is empty."));
                return;
            }
            Console.WriteLine("**There {0} {1} activations registered in the directory for this grain. The activations are:", (list.Count > 1) ? "are" : "is", list.Count);
            foreach (var tuple in list)
            {
                WriteStatus(string.Format("**Activation {0} on silo {1}", tuple.Activation, tuple.Silo));
            }
        }

        private static GrainId ConstructGrainId(string[] args, string operation)
        {
            if (args == null || args.Length < 2)
            {
                PrintUsage();
                return null;
            }
            string interfaceTypeCodeOrImplClassName = args[0];
            int interfaceTypeCodeDataLong;
            long implementationTypeCode;

            var grainTypeResolver = RuntimeClient.Current.GrainTypeResolver;
            if (int.TryParse(interfaceTypeCodeOrImplClassName, out interfaceTypeCodeDataLong))
            {
                // parsed it as int, so it is an interface type code.
                implementationTypeCode =
                    GetImplementation(grainTypeResolver, interfaceTypeCodeDataLong).GrainTypeCode;
            }
            else
            {
                // interfaceTypeCodeOrImplClassName is the implementation class name
                implementationTypeCode = GetImplementation(grainTypeResolver, interfaceTypeCodeOrImplClassName).GrainTypeCode;
            }

            var grainIdStr = args[1];
            GrainId grainId = null;
            long grainIdLong;
            Guid grainIdGuid;
            if (long.TryParse(grainIdStr, out grainIdLong))
                grainId = GrainId.GetGrainId(implementationTypeCode, grainIdLong);
            else if (Guid.TryParse(grainIdStr, out grainIdGuid))
                grainId = GrainId.GetGrainId(implementationTypeCode, grainIdGuid);
            else 
                grainId = GrainId.GetGrainId(implementationTypeCode, grainIdStr);
            
            WriteStatus(string.Format("**Full Grain Id to {0} is: GrainId = {1}", operation, grainId.ToFullString()));
            return grainId;
        }

        private static SiloAddress GetSiloAddress()
        {
            List<SiloAddress> silos = GetSiloAddresses();
            if (silos == null || silos.Count==0) return null;
            return silos.FirstOrDefault();
        }

        private static List<SiloAddress> GetSiloAddresses()
        {
            IList<Uri> gateways = GrainClient.Gateways;
            if (gateways.Count >= 1) 
                return gateways.Select(Utils.ToSiloAddress).ToList();

            WriteStatus(string.Format("**Retrieved only zero gateways from Client.Gateways"));
            return null;
        }

        private static string Pad(int value, int width)
        {
            return value.ToString("d").PadRight(width);
        }

        private static SiloAddress ParseSilo(string s)
        {
            return SiloAddress.FromParsableString(s);
        }

        public static void WriteStatus(string msg)
        {
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine(msg);
            Console.ResetColor();
        }

        internal static GrainClassData GetImplementation(IGrainTypeResolver grainTypeResolver, int interfaceId, string grainClassNamePrefix = null)
        {
            GrainClassData implementation;
            if (grainTypeResolver.TryGetGrainClassData(interfaceId, out implementation, grainClassNamePrefix)) return implementation;

            var loadedAssemblies = grainTypeResolver.GetLoadedGrainAssemblies();
            throw new ArgumentException(
                string.Format("Cannot find an implementation class for grain interface: {0}{2}. Make sure the grain assembly was correctly deployed and loaded in the silo.{1}",
                              interfaceId,
                              string.IsNullOrEmpty(loadedAssemblies) ? string.Empty : string.Format(" Loaded grain assemblies: {0}", loadedAssemblies),
                              string.IsNullOrEmpty(grainClassNamePrefix) ? string.Empty : ", grainClassNamePrefix=" + grainClassNamePrefix));
        }

        internal static GrainClassData GetImplementation(IGrainTypeResolver grainTypeResolver, string grainImplementationClassName)
        {
            GrainClassData implementation;
            if (!grainTypeResolver.TryGetGrainClassData(grainImplementationClassName, out implementation))
                throw new ArgumentException(string.Format("Cannot find an implementation grain class: {0}. Make sure the grain assembly was correctly deployed and loaded in the silo.", grainImplementationClassName));

            return implementation;
        }
    }
}
