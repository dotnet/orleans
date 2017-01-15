using System;
using System.Net;
using System.Threading;


namespace Orleans.Runtime.Host
{
    /// <summary>
    /// Host program for the Orleans Silo when it is being run on Windows Server machine.
    /// </summary>
    /// <seealso cref="SiloHost"/>
    public class WindowsServerHost : IDisposable
    {
        /// <summary> Debug flag, produces some additional log information while starting the Silo.
        /// </summary>
        public bool Debug 
        { 
            get { return SiloHost != null && SiloHost.Debug; } 
            set { SiloHost.Debug = value; } 
        }

        /// <summary> Reference to the SiloHost in this process. </summary>
        public SiloHost SiloHost { get; private set; }

        /// <summary> Initialization function -- loads silo config information. </summary>
        public void Init()
        {
            SiloHost.LoadOrleansConfig();
        }

        /// <summary>
        /// Run the Silo.
        /// </summary>
        /// <remarks>
        /// If the Silo starts up successfully, then this method will block and not return 
        /// until the silo shutdown event is triggered, or the silo shuts down for some other reason.
        /// If the silo fails to star, then a StartupError.txt summary file will be written, 
        /// and a process mini-dump will be created in the current working directory.
        /// </remarks>
        /// <returns>Returns <c>false</c> is Silo failed to start up correctly.</returns>
        public int Run()
        {
			return RunImpl();
        }

		/// <summary>
		/// Run the Silo.
		/// </summary>
		/// <param name="cancellationToken">Cancellation token.</param>
		/// <remarks>
		/// If the Silo starts up successfully, then this method will block and not return 
		/// until the silo shutdown event is triggered or the silo shuts down for some other reason or 
		/// an external request for cancellation has been issued.
		/// If the silo fails to star, then a StartupError.txt summary file will be written, 
		/// and a process mini-dump will be created in the current working directory.
		/// </remarks>
		/// <returns>Returns <c>false</c> is Silo failed to start up correctly.</returns>
		public int Run(CancellationToken cancellationToken)
		{
			return RunImpl(cancellationToken);
		}

		/// <summary>
		/// Run method helper.
		/// </summary>
		/// <param name="cancellationToken">Optional cancellation token.</param>
		/// <remarks>
		/// If the Silo starts up successfully, then this method will block and not return 
		/// until the silo shutdown event is triggered or the silo shuts down for some other reason or 
		/// an external request for cancellation has been issued.
		/// If the silo fails to star, then a StartupError.txt summary file will be written, 
		/// and a process mini-dump will be created in the current working directory.
		/// </remarks>
		/// <returns>Returns <c>false</c> is Silo failed to start up correctly.</returns>
		private int RunImpl(CancellationToken? cancellationToken = null)
		{
			bool ok;

			try
			{
				SiloHost.InitializeOrleansSilo();
				ok = SiloHost.StartOrleansSilo();

				if (ok)
				{
					ConsoleText.WriteStatus(string.Format("Successfully started Orleans silo '{0}' as a {1} node.", SiloHost.Name, SiloHost.Type));
					if (cancellationToken.HasValue)
						SiloHost.WaitForOrleansSiloShutdown(cancellationToken.Value);
					else
						SiloHost.WaitForOrleansSiloShutdown();
				}
				else
				{
					ConsoleText.WriteError(string.Format("Failed to start Orleans silo '{0}' as a {1} node.", SiloHost.Name, SiloHost.Type));
				}

				ConsoleText.WriteStatus(string.Format("Orleans silo '{0}' shutdown.", SiloHost.Name));
			}
			catch (Exception exc)
			{
				SiloHost.ReportStartupError(exc);
				LogManager.CreateMiniDump();
				ok = false;
			}

			return ok ? 0 : 1;
		}

        /// <summary>
        /// Parse command line arguments, to allow override of some silo runtime config settings.
        /// </summary>
        /// <param name="args">Command line arguments, as received by the Main program.</param>
        /// <returns></returns>
        public bool ParseArguments(string[] args)
        {
            string siloName = Dns.GetHostName(); // Default to machine name
            SiloHost = new SiloHost(siloName);

            int argPos = 1;
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if (a.StartsWith("-") || a.StartsWith("/"))
                {
                    switch (a.ToLowerInvariant())
                    {
                        case "/?":
                        case "/help":
                        case "-?":
                        case "-help":
                            // Query usage help
                            return false;
                        case "/debug":
                            SiloHost.Debug = true;
                            break;
                        default:
                            ConsoleText.WriteError("Bad command line arguments supplied: " + a);
                            return false;
                    }
                }
                else if (a.Contains("="))
                {
                    string[] split = a.Split('=');
                    if (String.IsNullOrEmpty(split[1]))
                    {
                        ConsoleText.WriteError("Bad command line arguments supplied: " + a);
                        return false;
                    }
                    switch (split[0].ToLowerInvariant())
                    {
                        case "deploymentid":
                            SiloHost.DeploymentId = split[1];
                            break;
                        case "deploymentgroup":
                            ConsoleText.WriteError("Ignoring deprecated command line argument: " + a);
                            break;
                        default:
                            ConsoleText.WriteError("Bad command line arguments supplied: " + a);
                            return false;
                    }
                }
                // unqualified arguments below
                else if (argPos == 1)
                {
                    SiloHost.Name = a;
                    argPos++;
                }
                else if (argPos == 2)
                {
                    SiloHost.ConfigFileName = a;
                    argPos++;
                }
                else
                {
                    // Too many command line arguments
                    ConsoleText.WriteError("Too many command line arguments supplied: " + a);
                    return false;
                }
            }

            return true;
        }

        /// <summary>
        /// Print usage info to console window, showing cmd-line params for OrleansHost.exe
        /// </summary>
        public void PrintUsage()
        {
            ConsoleText.WriteUsage(
@"USAGE: 
    OrleansHost.exe [<siloName> [<configFile>]] [DeploymentId=<idString>] [/debug]
Where:
    <siloName>      - Name of this silo in the Config file list (optional)
    <configFile>    - Path to the Config file to use (optional)
    DeploymentId=<idString> 
                    - Which deployment group this host instance should run in (optional)
    /debug          - Turn on extra debug output during host startup (optional)");
        }

        /// <summary>
        /// Dispose this host.
        /// </summary>
        public void Dispose()
        {
            Dispose(true);
        }

        protected virtual void Dispose(bool dispose)
        {
            SiloHost.Dispose();
            SiloHost = null;
        }
    }
}
