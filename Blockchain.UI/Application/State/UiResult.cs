namespace Blockchain.UI.Application.State;

public sealed record UiResult<T>(bool Success, T? Value, string Error)
{
    public static UiResult<T> Ok(T value) => new(true, value, string.Empty);
    public static UiResult<T> Fail(string error) => new(false, default, error);
}

public sealed record UiResult(bool Success, string Error)
{
    public static UiResult Ok() => new(true, string.Empty);
    public static UiResult Fail(string error) => new(false, error);
}
