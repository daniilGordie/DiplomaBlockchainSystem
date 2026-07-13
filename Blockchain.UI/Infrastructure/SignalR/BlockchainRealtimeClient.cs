using Blockchain.Node;
using Blockchain.UI.Application.Clients;
using Blockchain.UI.Services;
using Microsoft.AspNetCore.SignalR.Client;

namespace Blockchain.UI.Infrastructure.SignalR;

public sealed class BlockchainRealtimeClient : IBlockchainRealtimeClient, IAsyncDisposable
{
    private readonly KeyService _keyService;
    private HubConnection? _connection;
    private string? _currentProjectId;

    public BlockchainRealtimeClient(KeyService keyService)
    {
        _keyService = keyService;
    }

    public async Task ConnectAsync(string nodeUrl, Func<BlockModel, Task> onBlockReceived)
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }

        _connection = new HubConnectionBuilder()
            .WithUrl($"{nodeUrl.TrimEnd('/')}/blockchainHub")
            .WithAutomaticReconnect()
            .Build();

        _connection.On<BlockModel>("NewBlockBroadcast", block => onBlockReceived(block));
        _connection.Reconnected += async _ =>
        {
            if (_keyService.IsLoggedIn)
            {
                await JoinProjectsAsync(_currentProjectId);
            }
        };

        await _connection.StartAsync();
    }

    public async Task<bool> JoinProjectsAsync(string? currentProjectId)
    {
        _currentProjectId = currentProjectId;
        if (_connection == null ||
            _connection.State != HubConnectionState.Connected ||
            !_keyService.IsLoggedIn)
        {
            return false;
        }

        string registerChallenge = $"REGISTER:{_connection.ConnectionId}:{_keyService.UserName}";
        string registerSignature = _keyService.SignData(registerChallenge);
        bool registerOk = await _connection.InvokeAsync<bool>(
            "RegisterUser",
            _keyService.UserName,
            _keyService.PublicKey,
            registerSignature);

        if (!registerOk)
        {
            return false;
        }

        await _connection.InvokeAsync("JoinProject", "System");
        if (!string.IsNullOrWhiteSpace(currentProjectId))
        {
            await _connection.InvokeAsync("JoinProject", currentProjectId);
            string normalizedProjectId = NormalizeChannelId(currentProjectId);
            if (!string.Equals(normalizedProjectId, currentProjectId, StringComparison.Ordinal))
            {
                await _connection.InvokeAsync("JoinProject", normalizedProjectId);
            }
        }

        return true;
    }

    private static string NormalizeChannelId(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return "System";
        }

        var safeName = new string(channelId.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrEmpty(safeName) ? "System" : safeName;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection != null)
        {
            await _connection.DisposeAsync();
        }
    }
}
