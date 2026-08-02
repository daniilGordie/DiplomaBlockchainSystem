using System;
using System.Collections.Generic;
using Blockchain.Core.Contracts;

namespace Blockchain.Core
{
    public class BlockchainManager
    {
        private readonly IChainReader _chainReader;
        private readonly IChainWriter _chainWriter;
        private readonly IPendingBlockStore _pendingBlockStore;
        private readonly ISmartContractStateReader _smartContractState;
        private readonly PendingBlockConnector _pendingBlockConnector;
        private readonly PeerBlockValidator _peerBlockValidator;
        private static readonly DateTime GenesisTimestamp = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        public BlockchainManager(IBlockchainStore store)
            : this(store, store, store, store)
        {
        }

        public BlockchainManager(
            IChainReader chainReader,
            IChainWriter chainWriter,
            IPendingBlockStore pendingBlockStore,
            ISmartContractStateReader smartContractState)
            : this(
                chainReader,
                chainWriter,
                pendingBlockStore,
                smartContractState,
                new PendingBlockConnector(pendingBlockStore),
                new PeerBlockValidator(chainReader, smartContractState))
        {
        }

        public BlockchainManager(
            IChainReader chainReader,
            IChainWriter chainWriter,
            IPendingBlockStore pendingBlockStore,
            ISmartContractStateReader smartContractState,
            PendingBlockConnector pendingBlockConnector,
            PeerBlockValidator peerBlockValidator)
        {
            _chainReader = chainReader;
            _chainWriter = chainWriter;
            _pendingBlockStore = pendingBlockStore;
            _smartContractState = smartContractState;
            _pendingBlockConnector = pendingBlockConnector;
            _peerBlockValidator = peerBlockValidator;

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
                TimestampUnixSeconds = new DateTimeOffset(GenesisTimestamp).ToUnixTimeSeconds(),
                ChannelId = "System"
            };
            FinalizeBlock(genesisBlock);
            _chainWriter.SaveBlock(genesisBlock);
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

        public static void FinalizeBlock(Block block)
        {
            block.Nonce = 0;
            block.Hash = block.CalculateHash();
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
                if (!currentBlock.VerifySignature()) return false;
            }
            return true;
        }

    }
}
