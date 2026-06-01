using System.Collections.Generic;
using Blockchain.Core; 

namespace Blockchain.Core.Contracts
{
    public class ContractExecutor
    {
        private readonly List<ISmartContract> _contracts;

        public ContractExecutor()
        {
            _contracts = new List<ISmartContract>
            {
                new AccessControlContract(),
                new OracleContract(),
                new ReputationContract(),
                new TaskContract()
            };
        }

        public bool Execute(string data, string senderPublicKey, ISmartContractStateReader state)
        {
            foreach (var contract in _contracts)
            {
                if (!contract.Validate(data, senderPublicKey, state))
                {
                    System.Console.WriteLine($"[SmartContract] Transaction rejected by: {contract.Name}");
                    return false;
                }
            }
            return true;
        }
    }
}
