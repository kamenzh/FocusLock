using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using FocusLock.Security;
using Shared;

namespace FocusLock.Client;

public enum ApiFailureKind { Unreachable, Authentication, Api }
public sealed class FocusLockApiException(ApiFailureKind kind, string message, HttpStatusCode? statusCode = null)
    : Exception(message)
{
    public ApiFailureKind Kind { get; } = kind;
    public HttpStatusCode? StatusCode { get; } = statusCode;
}

public sealed class FocusLockApiClient(HttpClient http, ISecretStore secrets, Func<DateTimeOffset>? utcNow = null)
{
    private readonly Func<DateTimeOffset> now = utcNow ?? (() => DateTimeOffset.UtcNow);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public Task<LockStatus> GetStatusAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Get, "/api/status", [], ct);
    public Task<LockStatus> LockAsync(int minutes, CancellationToken ct = default) =>
        SendAsync(HttpMethod.Post, "/api/lock", JsonSerializer.SerializeToUtf8Bytes(new LockRequest(minutes), Json), ct);
    public Task<LockStatus> UnlockAsync(CancellationToken ct = default) => SendAsync(HttpMethod.Post, "/api/unlock", [], ct);

    private async Task<LockStatus> SendAsync(HttpMethod method, string path, byte[] body, CancellationToken ct)
    {
        try
        {
            var key = await secrets.ReadAsync(ct);
            using var request = new HttpRequestMessage(method, path);
            try
            {
                var timestamp = RequestSigning.Timestamp(now());
                var nonce = RequestSigning.NewNonce();
                request.Headers.Add(RequestSigning.TimestampHeader, timestamp);
                request.Headers.Add(RequestSigning.NonceHeader, nonce);
                request.Headers.Add(RequestSigning.SignatureHeader, Convert.ToBase64String(RequestSigning.Sign(key, method.Method, path, timestamp, nonce, body)));
            }
            finally { CryptographicOperations.ZeroMemory(key); }
            // The exact bytes signed above are the bytes sent; never serialize the object again.
            if (method == HttpMethod.Post)
            {
                request.Content = new ByteArrayContent(body);
                if (body.Length > 0) request.Content.Headers.ContentType = new("application/json");
            }
            using var response = await http.SendAsync(request, ct);
            if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                throw new FocusLockApiException(ApiFailureKind.Authentication, "Authentication failed.", response.StatusCode);
            if (!response.IsSuccessStatusCode)
            {
                ApiResult? error = null;
                try { error = await response.Content.ReadFromJsonAsync<ApiResult>(Json, ct); }
                catch (JsonException) { }
                throw new FocusLockApiException(ApiFailureKind.Api, error?.Message ?? $"Request failed ({(int)response.StatusCode}).", response.StatusCode);
            }
            return await response.Content.ReadFromJsonAsync<LockStatus>(Json, ct)
                ?? throw new FocusLockApiException(ApiFailureKind.Api, "The service returned an empty status response.");
        }
        catch (SecretUnavailableException exception) { throw new FocusLockApiException(ApiFailureKind.Authentication, exception.Message); }
        catch (HttpRequestException) { throw new FocusLockApiException(ApiFailureKind.Unreachable, "Cannot reach the service. Retrying automatically…"); }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        { throw new FocusLockApiException(ApiFailureKind.Unreachable, "The service timed out. Reconnecting to verify current state."); }
        catch (JsonException) { throw new FocusLockApiException(ApiFailureKind.Api, "The service returned an invalid response."); }
    }
}
