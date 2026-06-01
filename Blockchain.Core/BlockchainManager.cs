using System;
using System.Collections.Generic;
using Blockchain.Core.Contracts;
using Blockchain.Core.Constants;

namespace Blockchain.Core
{
    public class BlockchainManager
    {
        private readonly IChainReader _chainReader;
        private readonly IChainWriter _chainWriter;
        private readonly IPendingBlockStore _pendingBlockStore;
        private readonly ISmartContractStateReader _smartContractState;
        private readonly BlockMiner _blockMiner;
        private readonly PendingBlockConnector _pendingBlockConnector;
        private readonly PeerBlockValidator _peerBlockValidator;
        private readonly ChainAdoptionService _chainAdoptionService;
        private static readonly DateTime GenesisTimestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public BlockchainManager(IBlockchainStore store)
            : this(store, store, store, store, new UnsupportedReplayStoreFactory())
        {
        }

        public BlockchainManager(IBlockchainStore store, IReplayStoreFactory replayStoreFactory)
            : this(store, store, store, store, replayStoreFactory)
        {
        }

        public BlockchainManager(
            IChainReader chainReader,
            IChainWriter chainWriter,
            IPendingBlockStore pendingBlockStore,
            ISmartContractStateReader smartContractState,
            IReplayStoreFactory replayStoreFactory)
            : this(
                chainReader,
                chainWriter,
                pendingBlockStore,
                smartContractState,
                replayStoreFactory,
                new BlockMiner(),
                new PendingBlockConnector(pendingBlockStore),
                new PeerBlockValidator(chainReader, smartContractState),
                new ChainAdoptionService(chainReader, chainWriter, replayStoreFactory))
        {
        }

        public BlockchainManager(
            IChainReader chainReader,
            IChainWriter chainWriter,
            IPendingBlockStore pendingBlockStore,
            ISmartContractStateReader smartContractState,
            IReplayStoreFactory replayStoreFactory,
            BlockMiner blockMiner,
            PendingBlockConnector pendingBlockConnector,
            PeerBlockValidator peerBlockValidator,
            ChainAdoptionService chainAdoptionService)
        {
            _chainReader = chainReader;
            _chainWriter = chainWriter;
            _pendingBlockStore = pendingBlockStore;
            _smartContractState = smartContractState;
            _blockMiner = blockMiner;
            _pendingBlockConnector = pendingBlockConnector;
            _peerBlockValidator = peerBlockValidator;
            _chainAdoptionService = chainAdoptionService;

            if (!_chainReader.HasBlocks("System"))
            {
                AddGenesisBlock();
            }
            else if (!IsValidChain("System"))
            {
                Console.WriteLine("[BlockchainManager] Blockchain integrity check failed!");
            }
        }

        public Block? GetLatestBlock(string channelId = "System")
        {
            return _chainReader.GetLatestBlock(channelId);
        }

        public void AddGenesisBlock()
        {
            var genesisBlock = new Block
            {
                Index = 0,
                PreviousHash = "0",
                Data = "{\"Source\":\"System\",\"Message\":\"Nexus Genesis Block\"}",
                Timestamp = GenesisTimestamp,
                ChannelId = "System"
            };
            MineBlock(genesisBlock);
            _chainWriter.SaveBlock(genesisBlock);
        }

        public bool AddBlock(Block newBlock)
        {
            var latestBlock = GetLatestBlock(newBlock.ChannelId);

            int expectedIndex = latestBlock != null ? latestBlock.Index + 1 : 0;
            string expectedPrevHash = latestBlock != null ? latestBlock.Hash : "0";

            if (newBlock.Index != expectedIndex)
            {
                Console.WriteLine($"[Blockchain] Invalid Index. Expected {expectedIndex}, got {newBlock.Index}");
                return false;
            }

            if (newBlock.PreviousHash != expectedPrevHash)
            {
                Console.WriteLine("[Blockchain] Invalid PreviousHash.");
                return false;
            }

            if (!newBlock.VerifySignature())
            {
                Console.WriteLine("[Blockchain] Block signature verification failed!");
                return false;
            }

            MineBlock(newBlock);
            _chainWriter.SaveBlock(newBlock, newBlock.ChannelId);
            return true;
        }

        public bool ProcessPeerBlock(Block peerBlock)
        {
            var validation = _peerBlockValidator.Validate(peerBlock);
            if (validation.Status == PeerBlockValidationStatus.AlreadyAccepted)
            {
                return true;
            }

            if (validation.Status == PeerBlockValidationStatus.Pending)
            {
                Console.WriteLine($"[Blockchain] Channel '{peerBlock.ChannelId}' rejection: {validation.Reason}.");
                _pendingBlockStore.SavePendingBlock(peerBlock, validation.Reason);
                return false;
            }

            if (validation.Status == PeerBlockValidationStatus.Rejected)
            {
                Console.WriteLine($"[Blockchain] Rejected block {peerBlock.Hash}: {validation.Reason}.");
                return false;
            }

            _chainWriter.SaveBlock(peerBlock, peerBlock.ChannelId);
            _pendingBlockConnector.ConnectChildren(peerBlock.ChannelId, peerBlock.Hash, ProcessPeerBlock);
            return true;
        }

        private static bool IsPayloadChannelConsistent(string data, string channelId)
        {
            return BlockPayloadChannelPolicy.IsConsistent(data, channelId);
        }

        public bool TryAdoptChain(string channelId, List<Block> candidateChain)
        {
            return _chainAdoptionService.TryAdoptChain(channelId, candidateChain);
        }

        public void MineBlock(Block block)
        {
            _blockMiner.Mine(block);
        }

        public bool IsValidChain(string channelId = "System")
        {
            var chain = _chainReader.LoadChain(channelId);
            for (int i = 1; i < chain.Count; i++)
            {
                var currentBlock = chain[i];
                var previousBlock = chain[i - 1];

                if (currentBlock.Hash != currentBlock.CalculateHash()) return false;
                if (currentBlock.PreviousHash != previousBlock.Hash) return false;
                if (!currentBlock.Hash.StartsWith(NetworkParameters.TargetPrefix)) return false;
                if (!currentBlock.VerifySignature()) return false;
            }
            return true;
        }

        private sealed class UnsupportedReplayStoreFactory : IReplayStoreFactory
        {
            public IBlockchainStore CreateReplayStore()
            {
                throw new InvalidOperationException("Replay store factory is not configured.");
            }

            public void CleanupReplayStore(IBlockchainStore replayStore)
            {
            }
        }
    }
}
