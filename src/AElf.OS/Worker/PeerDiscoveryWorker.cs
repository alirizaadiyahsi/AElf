using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using AElf.OS.Network;
using AElf.OS.Network.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Org.BouncyCastle.Crypto.Engines;
using Volo.Abp.BackgroundWorkers;
using Volo.Abp.DependencyInjection;
using Volo.Abp.Threading;

namespace AElf.OS.Worker
{
    public class AssemblyPrinterWorker : PeriodicBackgroundWorkerBase, ISingletonDependency
    {
        public new ILogger<AssemblyPrinterWorker> Logger { get; set; }

        public AssemblyPrinterWorker(AbpTimer timer) : base(timer)
        {
            Timer.Period = 15_000;
        }

        protected override void DoWork()
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            
            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
            
            Logger.LogDebug($"Total assembly count: {assemblies.Length}.");

            var concernedAssemblies = assemblies
                .Where(a => !a.GetName().ToString().Contains("System")
                            && !a.GetName().ToString().Contains("Microsoft")
                            && !a.GetName().ToString().Contains("Microsoft")
                            && !a.GetName().ToString().Contains("Volo") )
                .GroupBy(k => k.GetName().Name);

            foreach (IGrouping<string, Assembly> asm in concernedAssemblies.Where(a => a.Count() > 1 || a.Key.Contains("HelloWorldContract")).OrderByDescending(a => a.Count()))
            {
                Logger.LogDebug($"Assembly: {asm.Key}, loaded count {asm.Count()} elements.");
            }

            assemblies = null;
            
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    public class PeerDiscoveryWorker : PeriodicBackgroundWorkerBase, ISingletonDependency
    {
        private readonly IPeerDiscoveryService _peerDiscoveryService;
        private readonly INetworkService _networkService;
        private readonly IReconnectionService _reconnectionService;

        public new ILogger<PeerDiscoveryWorker> Logger { get; set; }

        public PeerDiscoveryWorker(AbpTimer timer, IPeerDiscoveryService peerDiscoveryService,
            INetworkService networkService, IReconnectionService reconnectionService) : base(timer)
        {
            _peerDiscoveryService = peerDiscoveryService;
            Timer.Period = NetworkConstants.DefaultDiscoveryPeriod;

            _networkService = networkService;
            _reconnectionService = reconnectionService;

            Logger = NullLogger<PeerDiscoveryWorker>.Instance;
        }

        protected override async void DoWork()
        {
            await ProcessPeerDiscoveryJob();
        }

        internal async Task ProcessPeerDiscoveryJob()
        {
            var newNodes = await _peerDiscoveryService.DiscoverNodesAsync();

            if (newNodes == null || newNodes.Nodes.Count <= 0)
            {
                Logger.LogDebug("No new nodes discovered");
                return;
            }

            Logger.LogDebug($"New nodes discovered : {newNodes}.");

            foreach (var node in newNodes.Nodes)
            {
                try
                {
                    var reconnectingPeer = _reconnectionService.GetReconnectingPeer(node.Endpoint);

                    if (reconnectingPeer != null)
                    {
                        Logger.LogDebug($"Peer {node.Endpoint} is already in the reconnection queue.");
                        continue;
                    }
                    
                    if (_networkService.IsPeerPoolFull())
                    {
                        Logger.LogDebug("Peer pool is full, aborting add.");
                        break;
                    }
                    
                    await _networkService.AddPeerAsync(node.Endpoint);
                }
                catch (Exception e)
                {
                    Logger.LogError(e, $"Exception connecting to {node.Endpoint}.");
                }
            }
        }
    }
}