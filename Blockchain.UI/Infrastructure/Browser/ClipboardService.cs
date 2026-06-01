using Blockchain.UI.Application.Clients;
using Blockchain.UI.Application.State;
using Microsoft.JSInterop;

namespace Blockchain.UI.Infrastructure.Browser;

public sealed class ClipboardService : IClipboardService
{
    private readonly IJSRuntime _js;

    public ClipboardService(IJSRuntime js)
    {
        _js = js;
    }

    public async Task<UiResult> CopyTextAsync(string text)
    {
        try
        {
            await _js.InvokeVoidAsync("navigator.clipboard.writeText", text);
            return UiResult.Ok();
        }
        catch
        {
            try
            {
                bool copied = await _js.InvokeAsync<bool>("copyTextFallback", text);
                return copied
                    ? UiResult.Ok()
                    : UiResult.Fail("Copy failed. Please copy the text manually.");
            }
            catch (Exception ex)
            {
                return UiResult.Fail($"Clipboard error: {ex.Message}");
            }
        }
    }
}
