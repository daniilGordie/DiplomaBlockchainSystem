using Blockchain.UI.Application.State;

namespace Blockchain.UI.Application.Clients;

public interface IClipboardService
{
    Task<UiResult> CopyTextAsync(string text);
}
