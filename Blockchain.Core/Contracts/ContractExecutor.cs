using System.Collections.Generic;

namespace Blockchain.Core.Contracts
{
    public class ContractExecutor
    {
        private readonly List<ISmartContract> _contracts;

        public ContractExecutor()
        {
            _contracts = new List<ISmartContract>
            {
                new AccessControlContract()
               
            };
        }

        public bool Execute(string data, string senderPublicKey, DatabaseManager db)
        {
            foreach (var contract in _contracts)
            {
                if (!contract.Validate(data, senderPublicKey, db))
                {
                    System.Console.WriteLine($"[SmartContract] Transaction rejected by: {contract.Name}");
                    return false;
                }
            }
            return true;
        }
    }
}