using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Robust.Cdn.Config;

namespace Robust.Cdn.Helpers;

public sealed class RobustAuthHelper(IHttpContextAccessor accessor, IOptions<RobustOptions> options)
{
    public bool IsAuthValid([NotNullWhen(false)] out IActionResult? failureResult)
    {
        var tokenExpected = options.Value.PublishToken;
        if (string.IsNullOrEmpty(tokenExpected))
        {
            failureResult = new ObjectResult("Robust.PublishToken is not set")
            { StatusCode = StatusCodes.Status500InternalServerError };
            return false;
        }

        var context = accessor.HttpContext ?? throw new InvalidOperationException("Unable to get HttpContext");
        var authHeader = context.Request.Headers.Authorization;
        if (authHeader.Count == 0)
        {
            failureResult = new UnauthorizedResult();
            return false;
        }

        var auth = authHeader[0];
        if (auth == null || !auth.StartsWith("Bearer "))
        {
            failureResult = new UnauthorizedObjectResult("Need Bearer auth type");
            return false;
        }

        var token = auth["Bearer ".Length..];
        if (!StringsEqual(token, tokenExpected))
        {
            failureResult = new UnauthorizedObjectResult("Incorrect token");
            return false;
        }

        failureResult = null;
        return true;
    }

    private static bool StringsEqual(ReadOnlySpan<char> a, ReadOnlySpan<char> b)
    {
        return CryptographicOperations.FixedTimeEquals(
            MemoryMarshal.AsBytes(a),
            MemoryMarshal.AsBytes(b));
    }
}

