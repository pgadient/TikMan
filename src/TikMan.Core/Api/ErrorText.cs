using System.Security.Authentication;

namespace TikMan.Core.Api;

public static class ErrorText
{
    /// <summary>Condenses an exception chain into a single readable message –
    /// .NET likes to hide the actual cause behind "see inner exception".</summary>
    public static string Describe(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e is not null; e = e.InnerException)
        {
            var msg = e.Message.Trim().Replace(", see inner exception.", ".");
            if (msg.Length > 0 && !parts.Any(p => p.Contains(msg, StringComparison.OrdinalIgnoreCase)))
                parts.Add(msg);
        }
        return string.Join(" → ", parts);
    }

    public static bool IsTlsProblem(Exception ex)
    {
        for (var e = ex; e is not null; e = e.InnerException)
        {
            if (e is AuthenticationException) return true;
            if (e.Message.Contains("SSL", StringComparison.OrdinalIgnoreCase) ||
                e.Message.Contains("TLS", StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
