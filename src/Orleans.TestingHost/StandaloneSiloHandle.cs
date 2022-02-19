using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Configuration;

namespace Orleans.TestingHost
{
    /// <summary>
    /// A silo handle and factory which spawns a separate process for each silo.
    /// </summary>
    public class StandaloneSiloHandle : SiloHandle
    {
        private readonly StringBuilder _outputBuilder;
        private readonly TaskCompletionSource<bool> _startedEvent;
        private readonly TaskCompletionSource<bool> _outputCloseEvent;
        private readonly StringBuilder _errorBuilder;
        private readonly TaskCompletionSource<bool> _errorCloseEvent;
        private readonly EventHandler _processExitHandler;
        private bool isActive = true;
        private Task _runTask;

        /// <summary>
        /// The configuration key used to identify the process to launch.
        /// </summary>
        public const string ExecutablePathConfigKey = "ExecutablePath";

        /// <summary>Gets a reference to the silo host.</summary>
        private Process Process { get; set; }

        /// <inheritdoc />
        public override bool IsActive => isActive;
        
        public StandaloneSiloHandle(string siloName, IConfiguration configuration, string executablePath)
        {
            if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
            {
                throw new ArgumentException("Must provide at least one assembly path");
            }

            Name = siloName;

            // If the debugger is attached to this process, give it an opportunity to attach to the remote process.
            if (Debugger.IsAttached)
            {
                configuration["AttachDebugger"] = "true";
            }

            var serializedConfiguration = TestClusterHostFactory.SerializeConfiguration(configuration);

            Process = new Process();
            Process.StartInfo = new ProcessStartInfo(executablePath)
            {
                ArgumentList = { Process.GetCurrentProcess().Id.ToString(CultureInfo.InvariantCulture), serializedConfiguration },
                CreateNoWindow = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                WorkingDirectory = new FileInfo(executablePath).Directory.FullName,
                UseShellExecute = false,
            };

            _outputBuilder = new StringBuilder();
            _outputCloseEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _startedEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            Process.OutputDataReceived += (s, e) =>
            {
                if (e.Data == null)
                {
                    _outputCloseEvent.SetResult(true);
                }
                else
                {
                    // Read standard output from the process for status updates.
                    if (!_startedEvent.Task.IsCompleted)
                    {
                        if (e.Data.StartsWith(StandaloneSiloHost.SiloAddressLog, StringComparison.Ordinal))
                        {
                            SiloAddress = Orleans.Runtime.SiloAddress.FromParsableString(e.Data.Substring(StandaloneSiloHost.SiloAddressLog.Length));
                        }
                        else if (e.Data.StartsWith(StandaloneSiloHost.GatewayAddressLog, StringComparison.Ordinal))
                        {
                            GatewayAddress = Orleans.Runtime.SiloAddress.FromParsableString(e.Data.Substring(StandaloneSiloHost.GatewayAddressLog.Length));
                        }
                        else if (e.Data.StartsWith(StandaloneSiloHost.StartedLog, StringComparison.Ordinal))
                        {
                            _startedEvent.TrySetResult(true);
                        }
                    }

                    lock (_outputBuilder)
                    {
                        _outputBuilder.AppendLine(e.Data);
                    }
                }
            };

            _errorBuilder = new StringBuilder();
            _errorCloseEvent = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

           Process.ErrorDataReceived += (s, e) =>
           {
                if (e.Data == null)
                {
                    _errorCloseEvent.SetResult(true);
                }
                else
                {
                   lock (_errorBuilder)
                   {
                       _errorBuilder.AppendLine(e.Data);
                   }
                }
           };

            var selfReference = new WeakReference<StandaloneSiloHandle>(this);
            _processExitHandler = (o, e) =>
            {
                if (selfReference.TryGetTarget(out var target))
                {
                    try
                    {
                        target.Process.Kill(entireProcessTree: true);
                    }
                    catch
                    {

                    }
                }
            };
            AppDomain.CurrentDomain.ProcessExit += _processExitHandler;
        }

        /// <summary>
        /// Spawns a new process to host a silo, using the executable provided in the configuration's "ExecutablePath" property as the entry point.
        /// </summary>
        public static async Task<SiloHandle> Create(
            string siloName,
            IConfiguration configuration)
        {
            var executablePath = configuration[ExecutablePathConfigKey];
            var result = new StandaloneSiloHandle(siloName, configuration, executablePath);
            await result.StartAsync();
            return result;
        }

        /// <summary>
        /// Creates a delegate which spawns a silo in a new process, using the provided executable as the entry point for that silo.
        /// </summary>
        /// <param name="executablePath">The entry point for spawned silos.</param>
        public static Func<string, IConfiguration, Task<SiloHandle>> CreateDelegate(string executablePath)
        {
            return async (siloName, configuration) =>
            {
                var result = new StandaloneSiloHandle(siloName, configuration, executablePath);
                await result.StartAsync();
                return result;
            };
        }

        /// <summary>
        /// Creates a delegate which spawns a silo in a new process, with the provided assembly (or its executable counterpart, if it is a library) being the entry point for that silo.
        /// </summary>
        /// <param name="assembly">The entry point for spawned silos. If the provided assembly is a library (dll), then its executable sibling assembly will be invoked instead.</param>
        public static Func<string, IConfiguration, Task<SiloHandle>> CreateForAssembly(Assembly assembly)
        {
            var executablePath = assembly.Location;
            var originalFileInfo = new FileInfo(executablePath);
            if (!originalFileInfo.Exists)
            {
                throw new FileNotFoundException($"Cannot find assembly location for assembly {assembly}. Location property returned \"{executablePath}\"");
            }

            FileInfo target;
            if (string.Equals(".dll", originalFileInfo.Extension))
            {
                if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
                {
                    target = new FileInfo(Path.GetFileNameWithoutExtension(originalFileInfo.FullName) + ".exe");
                }
                else
                {
                    // On unix-like operating systems, executables generally do not have an extension.
                    target = new FileInfo(Path.GetFileNameWithoutExtension(originalFileInfo.FullName));
                }
            }
            else
            {
                target = originalFileInfo;
            }

            if (!target.Exists)
            {
                throw new FileNotFoundException($"Target assembly \"{target.FullName}\" does not exist");
            }

            return CreateDelegate(target.FullName);
        }

        private async Task StartAsync()
        {
            try
            {
                if (!Process.Start())
                {
                    throw new InvalidOperationException("No process was started");
                }
            }
            catch (Exception)
            {
                isActive = false;
                throw;
            }

            _runTask = Task.Run(async () =>
            {
                try
                {
                    Process.BeginOutputReadLine();
                    Process.BeginErrorReadLine();

                    var waitForExit = Task.Factory.StartNew(
                        process => ((Process)process).WaitForExit(-1),
                        Process,
                        CancellationToken.None,
                        TaskCreationOptions.LongRunning | TaskCreationOptions.RunContinuationsAsynchronously,
                        TaskScheduler.Default);
                    await Task.WhenAll(waitForExit, _outputCloseEvent.Task, _errorCloseEvent.Task);

                    if (!await waitForExit)
                    {
                        try
                        {
                            Process.Kill();
                        }
                        catch
                        {
                        }
                    }
                }
                catch (Exception exception)
                {
                    _startedEvent.TrySetException(exception);
                }
            });

            var task = await Task.WhenAny(_startedEvent.Task, _outputCloseEvent.Task, _errorCloseEvent.Task);
            if (!ReferenceEquals(task, _startedEvent.Task))
            {
                string output;
                lock (_outputBuilder)
                {
                    output = _outputBuilder.ToString();
                }

                string error;
                lock (_errorBuilder)
                {
                    error = _errorBuilder.ToString();
                }

                throw new Exception($"Process failed to start correctly.\nOutput:\n{output}\nError:\n{error}");
            }
        }

        /// <inheritdoc />
        public override async Task StopSiloAsync(bool stopGracefully)
        {
            var cancellation = new CancellationTokenSource();
            var ct = cancellation.Token;

            if (!stopGracefully) cancellation.Cancel();

            await StopSiloAsync(ct);
        }

        /// <inheritdoc />
        public override async Task StopSiloAsync(CancellationToken ct)
        {
            if (!IsActive) return;

            try
            {
                if (ct.IsCancellationRequested)
                {
                    this.Process?.Kill();
                }
                else
                {
                    using var registration = ct.Register(() =>
                    {
                        var process = this.Process;
                        if (process is not null)
                        {
                            process.Kill();
                        }
                    });

                    await this.Process.StandardInput.WriteLineAsync(StandaloneSiloHost.ShutdownCommand);
                    var linkedCts = new CancellationTokenSource();
                    await Task.WhenAny(_runTask, Task.Delay(TimeSpan.FromMinutes(2), linkedCts.Token));
                }
            }
            finally
            {
                this.isActive = false;
            }
        }

        /// <inheritdoc />
        protected override void Dispose(bool disposing)
        {
            if (!this.IsActive) return;

            if (disposing)
            {
                try
                {
                    StopSiloAsync(true).GetAwaiter().GetResult();
                }
                catch
                {
                }

                this.Process?.Dispose();
            }

            AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        }

        /// <inheritdoc />
        public override async ValueTask DisposeAsync()
        {
            if (!this.IsActive) return;

            await StopSiloAsync(true).ConfigureAwait(false);
            this.Process?.Dispose();
            AppDomain.CurrentDomain.ProcessExit -= _processExitHandler;
        }
    }
}