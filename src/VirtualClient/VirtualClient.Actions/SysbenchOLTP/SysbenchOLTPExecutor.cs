﻿// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

namespace VirtualClient.Actions
{
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net;
    using System.Runtime.InteropServices;
    using System.Threading;
    using System.Threading.Tasks;
    using Microsoft.Extensions.DependencyInjection;
    using VirtualClient.Common;
    using VirtualClient.Common.Extensions;
    using VirtualClient.Common.Telemetry;
    using VirtualClient.Contracts;

    /// <summary>
    /// The Sysbench workload executor.
    /// </summary>
    public class SysbenchOLTPExecutor : VirtualClientComponent
    {
        private readonly IStateManager stateManager;

        /// <summary>
        /// Constructor for <see cref="SysbenchOLTPExecutor"/>
        /// </summary>
        /// <param name="dependencies">Provides required dependencies to the component.</param>
        /// <param name="parameters">Parameters defined in the profile or supplied on the command line.</param>
        public SysbenchOLTPExecutor(IServiceCollection dependencies, IDictionary<string, IConvertible> parameters)
             : base(dependencies, parameters)
        {
            this.stateManager = this.SystemManager.StateManager;
            // Supported roles for this client/server workload.
            this.SupportedRoles = new List<string>
            {
                ClientRole.Client,
                ClientRole.Server
            };
        }

        /// <summary>
        /// Parameter defines the scenario to use for the MySQL user accounts used
        /// to create the DB and run transactions against it.
        /// </summary>
        public string DatabaseScenario
        {
            get
            {
                return this.Parameters.GetValue<string>(nameof(this.DatabaseScenario), SysbenchOLTPScenario.Default);
            }
        }

        /// <summary>
        /// Client used to communicate with the hosted instance of the
        /// Virtual Client API at server side.
        /// </summary>
        public IApiClient ServerApiClient { get; set; }

        /// <summary>
        /// Cancellation Token Source for Server.
        /// </summary>
        protected CancellationTokenSource ServerCancellationSource { get; set; }

        /// <summary>
        /// Server IpAddress on which MySQL Server runs.
        /// </summary>
        protected string ServerIpAddress { get; set; }

        /// <summary>
        /// Server IpAddress on which the client runs.
        /// </summary>
        protected string ClientIpAddress { get; set; }

        /// <summary>
        /// An interface that can be used to communicate with the underlying system.
        /// </summary>
        protected ISystemManagement SystemManager => this.Dependencies.GetService<ISystemManagement>();

        /// <summary>
        /// Executes the Sysbench workload.
        /// </summary>
        protected override Task ExecuteAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            throw new NotImplementedException();
        }

        /// <summary>
        /// Initializes the environment for execution of the Sysbench workload.
        /// </summary>
        protected override async Task InitializeAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            this.CheckPlatformSupport();
            await this.CheckDistroSupportAsync(telemetryContext, cancellationToken)
                .ConfigureAwait(false);

            await this.InitializeExecutablesAsync(cancellationToken);

            if (this.IsMultiRoleLayout())
            {
                ClientInstance clientInstance = this.GetLayoutClientInstance();
                string layoutIPAddress = clientInstance.IPAddress;

                this.ThrowIfLayoutClientIPAddressNotFound(layoutIPAddress);
                this.ThrowIfRoleNotSupported(clientInstance.Role);
            }
        }

        /// <summary>
        /// Initializes API client.
        /// </summary>
        protected void InitializeApiClients()
        {
            IApiClientManager clientManager = this.Dependencies.GetService<IApiClientManager>();
            bool isSingleVM = !this.IsMultiRoleLayout();

            if (isSingleVM)
            {
                this.ServerIpAddress = IPAddress.Loopback.ToString();
                this.ServerApiClient = clientManager.GetOrCreateApiClient(this.ServerIpAddress, IPAddress.Loopback);
            }
            else
            {
                ClientInstance serverInstance = this.GetLayoutClientInstances(ClientRole.Server).First();
                IPAddress.TryParse(serverInstance.IPAddress, out IPAddress serverIPAddress);

                this.ServerIpAddress = serverIPAddress.ToString();
                this.ServerApiClient = clientManager.GetOrCreateApiClient(serverIPAddress.ToString(), serverIPAddress);
                this.RegisterToSendExitNotifications($"{this.TypeName}.ExitNotification", this.ServerApiClient);

                ClientInstance clientInstance = this.GetLayoutClientInstances(ClientRole.Client).First();
                IPAddress.TryParse(clientInstance.IPAddress, out IPAddress clientIPAddress);

                this.ClientIpAddress = clientIPAddress.ToString();
            }
        }

        /// <summary>
        /// Initializes the workload executables on the system (e.g. attributes them as executable).
        /// </summary>
        protected async Task InitializeExecutablesAsync(CancellationToken cancellationToken)
        {
            if (this.Platform == PlatformID.Unix)
            {
                string scriptsDirectory = this.PlatformSpecifics.GetScriptPath("sysbencholtp");

                await this.SystemManager.MakeFileExecutableAsync(
                    this.Combine(scriptsDirectory, "balanced-Server.sh"),
                    this.Platform,
                    cancellationToken);

                await this.SystemManager.MakeFileExecutableAsync(
                    this.Combine(scriptsDirectory, "balanced-Client.sh"),
                    this.Platform,
                    cancellationToken);

                await this.SystemManager.MakeFileExecutableAsync(
                    this.Combine(scriptsDirectory, "in-Memory.sh"),
                    this.Platform,
                    cancellationToken);
            }
        }

        /// <summary>
        /// Executes the commands.
        /// </summary>
        /// <param name="pathToExe">Executable name</param>
        /// <param name="command">Command that needs to be executed</param>
        /// <param name="workingDirectory">The directory where we want to execute the command</param>
        /// <param name="cancellationToken">A token that can be used to cancel the operation.</param>
        /// <returns>String output of the command.</returns>
        protected async Task<string> ExecuteCommandAsync<TExecutor>(string pathToExe, string command, string workingDirectory, CancellationToken cancellationToken)
            where TExecutor : VirtualClientComponent
        {
            string output = string.Empty;
            if (!cancellationToken.IsCancellationRequested)
            {
                this.Logger.LogTraceMessage($"Executing process '{pathToExe}' '{command}'  at directory '{workingDirectory}'.");

                EventContext telemetryContext = EventContext.Persisted()
                    .AddContext("command", pathToExe)
                    .AddContext("command", command);

                await this.Logger.LogMessageAsync($"{typeof(TExecutor).Name}.ExecuteProcess", telemetryContext, async () =>
                {
                    using (IProcessProxy process = this.SystemManager.ProcessManager.CreateElevatedProcess(this.Platform, pathToExe, command, workingDirectory))
                    {
                        this.CleanupTasks.Add(() => process.SafeKill());
                        process.RedirectStandardOutput = true;
                        await process.StartAndWaitAsync(cancellationToken).ConfigureAwait(false);

                        if (!cancellationToken.IsCancellationRequested)
                        {
                            await this.LogProcessDetailsAsync(process, telemetryContext);
                            process.ThrowIfErrored<WorkloadException>(errorReason: ErrorReason.WorkloadFailed);
                        }

                        output = process.StandardOutput.ToString();
                    }

                }).ConfigureAwait(false);
            }

            return output;
        }

        private void CheckPlatformSupport()
        {
            switch (this.Platform)
            {
                case PlatformID.Unix:
                    break;
                default:
                    throw new WorkloadException(
                        $"The Sysbench OLTP workload is currently not supported on the current platform/architecture " +
                        $"{PlatformSpecifics.GetPlatformArchitectureName(this.Platform, this.CpuArchitecture)}." +
                        $" Supported platform/architectures include: " +
                        $"{PlatformSpecifics.GetPlatformArchitectureName(PlatformID.Unix, Architecture.X64)}, " +
                        $"{PlatformSpecifics.GetPlatformArchitectureName(PlatformID.Unix, Architecture.Arm64)}",
                        ErrorReason.PlatformNotSupported);
            }
        }

        private async Task CheckDistroSupportAsync(EventContext telemetryContext, CancellationToken cancellationToken)
        {
            if (this.Platform == PlatformID.Unix)
            {
                var linuxDistributionInfo = await this.SystemManager.GetLinuxDistributionAsync(cancellationToken)
                .ConfigureAwait(false);

                if (!cancellationToken.IsCancellationRequested)
                {
                    switch (linuxDistributionInfo.LinuxDistribution)
                    {
                        case LinuxDistribution.Ubuntu:
                        case LinuxDistribution.Debian:
                            break;
                        default:
                            throw new WorkloadException(
                                $"The Sysbench OLTP workload is not supported on the current Linux distro - " +
                                $"{linuxDistributionInfo.LinuxDistribution}.  Supported distros include:" +
                                $"{Enum.GetName(typeof(LinuxDistribution), LinuxDistribution.Ubuntu)},{Enum.GetName(typeof(LinuxDistribution), LinuxDistribution.Debian)}",
                                ErrorReason.LinuxDistributionNotSupported);
                    }
                }
            }
        }

        internal class SysbenchOLTPState : State
        {
            public SysbenchOLTPState(IDictionary<string, IConvertible> properties = null)
                : base(properties)
            {
            }

            public bool SysbenchInitialized
            {
                get
                {
                    return this.Properties.GetValue<bool>(nameof(SysbenchOLTPState.SysbenchInitialized), false);
                }

                set
                {
                    this.Properties[nameof(SysbenchOLTPState.SysbenchInitialized)] = value;
                }
            }

            public bool DatabaseScenarioInitialized
            {
                get
                {
                    return this.Properties.GetValue<bool>(nameof(SysbenchOLTPState.DatabaseScenarioInitialized), false);
                }

                set
                {
                    this.Properties[nameof(SysbenchOLTPState.DatabaseScenarioInitialized)] = value;
                }
            }

            public string DiskPathsArgument
            {
                get
                {
                    return this.Properties.GetValue<string>(nameof(SysbenchOLTPState.DiskPathsArgument), string.Empty);
                }

                set
                {
                    this.Properties[nameof(SysbenchOLTPState.DiskPathsArgument)] = value;
                }
            }

            public bool DatabaseInitialized
            {
                get
                {
                    return this.Properties.GetValue<bool>(nameof(SysbenchOLTPState.DatabaseInitialized), false);
                }

                set
                {
                    this.Properties[nameof(SysbenchOLTPState.DatabaseInitialized)] = value;
                }
            }
        }

        /// <summary>
        /// Defines the Sysbench OLTP benchmark scenario.
        /// </summary>
        internal class SysbenchOLTPScenario
        {
            public const string Balanced = nameof(Balanced);

            public const string InMemory = nameof(InMemory);

            public const string Default = nameof(Default);
        }
    }
}