using FocusLock.Security;
using Microsoft.AspNetCore.Http.Features;
using Shared;

namespace LockService.Security;

public sealed class ProtectedEndpoint { }
public sealed class EmptyBodyEndpoint { }

public sealed class AuthenticationMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, IRequestAuthenticator authenticator,
        ISecurityAudit audit, ILogger<AuthenticationMiddleware> logger)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ProtectedEndpoint>() is null)
        {
            await next(context);
            return;
        }
        var request = context.Request;
        var rawTarget = context.Features.Get<IHttpRequestFeature>()?.RawTarget;
        var rawPath = string.IsNullOrEmpty(rawTarget) ? request.Path.Value ?? "" : rawTarget;
        AuthenticationFailure failure;
        byte[] body;
        if (request.QueryString.HasValue || rawPath != request.Path.Value || request.ContentLength > RequestSigning.MaxBodyBytes)
            failure = AuthenticationFailure.Malformed;
        else
        {
            // Read a bounded buffer before routing/model binding; sign the wire bytes, not reserialized JSON.
            using var buffer = new MemoryStream();
            var chunk = new byte[1024];
            int count;
            while ((count = await request.Body.ReadAsync(chunk, context.RequestAborted)) > 0)
            {
                buffer.Write(chunk, 0, count);
                if (buffer.Length > RequestSigning.MaxBodyBytes) break;
            }
            body = buffer.ToArray();
            failure = await authenticator.AuthenticateAsync(request.Method, rawPath, body,
                SingleHeader(request, RequestSigning.TimestampHeader), SingleHeader(request, RequestSigning.NonceHeader),
                SingleHeader(request, RequestSigning.SignatureHeader), context.RequestAborted);
            if (failure == AuthenticationFailure.None)
            {
                // POST /unlock and GET /status have no body at all; no account selectors are accepted.
                if (context.GetEndpoint()?.Metadata.GetMetadata<EmptyBodyEndpoint>() is not null && body.Length != 0)
                {
                    context.Response.StatusCode = StatusCodes.Status400BadRequest;
                    await context.Response.WriteAsJsonAsync(new ApiResult(false, "This endpoint requires an empty body."));
                    return;
                }
                var original = request.Body;
                using var authenticatedBody = new MemoryStream(body, writable: false);
                request.Body = authenticatedBody;
                try { await next(context); }
                finally { request.Body = original; }
                return;
            }
        }
        audit.Record(SecurityEvent.AuthenticationRejected, failure.ToString());
        if (failure == AuthenticationFailure.Replay) audit.Record(SecurityEvent.ReplayRejected);
        logger.LogWarning("Protected request authentication rejected: {ReasonCode}", failure);
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        await context.Response.WriteAsJsonAsync(new ApiResult(false, "Authentication failed."));
    }

    private static string? SingleHeader(HttpRequest request, string name) =>
        request.Headers.TryGetValue(name, out var values) && values.Count == 1 ? values[0] : null;
}
