using Microsoft.AspNetCore.SignalR;

namespace Blockchain.Node.Hubs
{
    public class BlockchainHub : Hub
    {
        public async Task JoinProject(string projectId) =>
            await Groups.AddToGroupAsync(Context.ConnectionId, projectId);

        public async Task RegisterUser(string userName) =>
            await Groups.AddToGroupAsync(Context.ConnectionId, $"USER_{userName}");
    }
}