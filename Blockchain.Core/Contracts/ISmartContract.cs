using Blockchain.Core;

namespace Blockchain.Core.Contracts
{
    public interface ISmartContract
    {
        string Name { get; }

        bool Validate(string data, string senderPublicKey, DatabaseManager db);
    }
}