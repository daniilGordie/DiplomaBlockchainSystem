using System;
using System.Collections.Generic;
using System.Linq;
using Blockchain.Core.Contracts;
using Blockchain.Core.Constants; 

namespace Blockchain.Core
{
    public class BlockchainManager
    {
        private readonly DatabaseManager _db;
        private readonly ContractExecutor _executor;
        public List<Block> Chain { get; private set; }

        public BlockchainManager(string dbFileName = "nexus_node.db")
        {
            _db = new DatabaseManager(dbFileName);
            _executor = new ContractExecutor();
            Chain = _db.LoadChain();

            if (Chain.Count == 0)
            {
                AddGenesisBlock();
            }
            else if (!IsValidChain())
            {
                Console.WriteLine(" Blockchain integrity check failed!");
            }
        }

        public Block GetLatestBlock()
        {
            return Chain.LastOrDefault();
        }

        public void AddGenesisBlock()
        {
            var genesisBlock = new Block
            {
                Index = 0,
                PreviousHash = "0",
                Data = "{\"Source\":\"System\",\"Message\":\"Nexus Genesis Block\"}",
                Timestamp = DateTime.UtcNow
            };
            MineBlock(genesisBlock);
            Chain.Add(genesisBlock);
            _db.SaveBlock(genesisBlock);
        }

        public bool AddBlock(Block newBlock)
        {
            var latestBlock = GetLatestBlock();
            newBlock.Index = latestBlock.Index + 1;
            newBlock.PreviousHash = latestBlock.Hash;

            if (!_executor.Execute(newBlock.Data, newBlock.ValidatorPublicKey, _db))
            {
                return false;
            }

            MineBlock(newBlock);
            Chain.Add(newBlock);
            _db.SaveBlock(newBlock);
            return true;
        }

        public bool ProcessPeerBlock(Block peerBlock)
        {
            var latestBlock = GetLatestBlock();
            if (peerBlock.Index != latestBlock.Index + 1 || peerBlock.PreviousHash != latestBlock.Hash)
            {
                return false;
            }

            if (!_executor.Execute(peerBlock.Data, peerBlock.ValidatorPublicKey, _db))
            {
                return false;
            }

            if (!peerBlock.Hash.StartsWith(ConsensusRules.TargetPrefix))
            {
                return false;
            }

            Chain.Add(peerBlock);
            _db.SaveBlock(peerBlock);
            return true;
        }

        public void MineBlock(Block block)
        {
            do
            {
                block.Nonce++;
                block.Hash = CalculateHash(block);
            }
            while (!block.Hash.StartsWith(ConsensusRules.TargetPrefix));
        }

        public bool IsValidChain()
        {
            for (int i = 1; i < Chain.Count; i++)
            {
                var currentBlock = Chain[i];
                var previousBlock = Chain[i - 1];

                if (currentBlock.Hash != CalculateHash(currentBlock)) return false;
                if (currentBlock.PreviousHash != previousBlock.Hash) return false;

               
                if (!currentBlock.Hash.StartsWith(ConsensusRules.TargetPrefix)) return false;
            }
            return true;
        }

        public void SubmitToMempool(string txId, string json)
        {
            _db.SaveToMempool(txId, json);
        }

        private string CalculateHash(Block block)
        {
            using (var sha256 = System.Security.Cryptography.SHA256.Create())
            {
                string rawData = $"{block.PreviousHash}{block.Timestamp:O}{block.Data}{block.ValidatorPublicKey}{block.Nonce}";

                byte[] bytes = sha256.ComputeHash(System.Text.Encoding.UTF8.GetBytes(rawData));

                return BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
            }
        }
    }
}