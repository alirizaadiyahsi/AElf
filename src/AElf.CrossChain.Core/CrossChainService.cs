using System.Collections.Generic;
using System.Threading.Tasks;
using Acs7;
using AElf.CrossChain.Cache.Application;
using AElf.CrossChain.Indexing.Infrastructure;
using AElf.Kernel;
using AElf.Types;
using Google.Protobuf.WellKnownTypes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace AElf.CrossChain
{
    internal class CrossChainService : ICrossChainService
    {
        private readonly IIrreversibleBlockStateProvider _irreversibleBlockStateProvider;
        private readonly ICrossChainCacheEntityService _crossChainCacheEntityService;
        private readonly IReaderFactory _readerFactory;
        public ILogger<CrossChainService> Logger { get; set; }

        public CrossChainService(IIrreversibleBlockStateProvider irreversibleBlockStateProvider,
            IReaderFactory readerFactory, ICrossChainCacheEntityService crossChainCacheEntityService)
        {
            _irreversibleBlockStateProvider = irreversibleBlockStateProvider;
            _readerFactory = readerFactory;
            _crossChainCacheEntityService = crossChainCacheEntityService;
        }

        public IOptionsMonitor<CrossChainConfigOptions> CrossChainConfigOptions { get; set; }

        public async Task FinishInitialSyncAsync()
        {
            CrossChainConfigOptions.CurrentValue.CrossChainDataValidationIgnored = false;
            var isReadyToCreateChainCache =
                await _irreversibleBlockStateProvider.ValidateIrreversibleBlockExistingAsync();
            if (!isReadyToCreateChainCache)
                return;
            var libIdHeight = await _irreversibleBlockStateProvider.GetLastIrreversibleBlockHashAndHeightAsync();
            var chainIdHeightPairs =
                await GetAllChainIdHeightPairsAsync(libIdHeight.BlockHash, libIdHeight.BlockHeight);
            foreach (var chainIdHeight in chainIdHeightPairs.IdHeightDict)
            {
                // register new chain
                _crossChainCacheEntityService.RegisterNewChain(chainIdHeight.Key, chainIdHeight.Value);
            }
        }

        public Dictionary<int, long> GetNeededChainIdAndHeightPairs()
        {
            var chainIdList = _crossChainCacheEntityService.GetCachedChainIds();
            var dict = new Dictionary<int, long>();
            foreach (var chainId in chainIdList)
            {
                var neededChainHeight = _crossChainCacheEntityService.GetTargetHeightForChainCacheEntity(chainId);
                if (neededChainHeight < Constants.GenesisBlockHeight)
                    continue;
                dict.Add(chainId, neededChainHeight);
            }

            return dict;
        }

        public async Task<Block> GetNonIndexedBlockAsync(long height)
        {
            return await _irreversibleBlockStateProvider.GetNotIndexedIrreversibleBlockByHeightAsync(height);
        }

        public async Task<ChainInitializationData> GetChainInitializationDataAsync(int chainId)
        {
            var libDto = await _irreversibleBlockStateProvider.GetLastIrreversibleBlockHashAndHeightAsync();
            return await _readerFactory.Create(libDto.BlockHash, libDto.BlockHeight).GetChainInitializationData
                .CallAsync(new SInt32Value
                {
                    Value = chainId
                });
        }

        public async Task UpdateWithLibAsync(Hash blockHash, long blockHeight)
        {
            if (CrossChainConfigOptions.CurrentValue.CrossChainDataValidationIgnored
                || blockHeight <= Constants.GenesisBlockHeight)
                return;

            var chainIdHeightPairs = await GetAllChainIdHeightPairsAsync(blockHash, blockHeight);

            foreach (var chainIdHeight in chainIdHeightPairs.IdHeightDict)
            {
                // register new chain
                _crossChainCacheEntityService.RegisterNewChain(chainIdHeight.Key, chainIdHeight.Value);

                // clear cross chain cache
                _crossChainCacheEntityService.ClearOutOfDateCrossChainCache(chainIdHeight.Key, chainIdHeight.Value);
                Logger.LogDebug(
                    $"Clear chain {ChainHelper.ConvertChainIdToBase58(chainIdHeight.Key)} cache by height {chainIdHeight.Value}");
            }
        }

        private async Task<SideChainIdAndHeightDict> GetAllChainIdHeightPairsAsync(Hash blockHash, long blockHeight)
        {
            return await _readerFactory.Create(blockHash, blockHeight).GetAllChainsIdAndHeight
                .CallAsync(new Empty());
        }
    }
}