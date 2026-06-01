namespace Blockchain.Core;

public static class ChannelName
{
    public static string Normalize(string channelId)
    {
        if (string.IsNullOrWhiteSpace(channelId))
        {
            return "System";
        }

        var safeName = new string(channelId.Where(char.IsLetterOrDigit).ToArray());
        return string.IsNullOrEmpty(safeName) ? "System" : safeName;
    }
}
