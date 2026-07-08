namespace TikMan.Core.Api;

/// <summary>Error response from the RouterOS REST API (e.g. 401 for wrong credentials) –
/// as opposed to pure transport errors (timeout, connection drop).</summary>
public class RouterOsApiException : Exception
{
    public int StatusCode { get; }

    public RouterOsApiException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
