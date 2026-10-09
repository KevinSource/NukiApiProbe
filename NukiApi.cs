using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace NukiApiProbe;

/// <summary>Result of one Nuki Web API call. Status 0 means the request never got a response.</summary>
public record ApiResult(int Status, string Body)
{
    public bool Ok => Status is >= 200 and < 300;
}

/// <summary>Thin wrapper over https://api.nuki.io that never throws; every call returns status + body.</summary>
public sealed class NukiApi : IDisposable
{
    private readonly HttpClient _http;

    public NukiApi(string token)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri("https://api.nuki.io/"),
            Timeout = TimeSpan.FromSeconds(30)
        };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Trim());
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public Task<ApiResult> GetAsync(string path) => SendAsync(HttpMethod.Get, path, null);
    public Task<ApiResult> PutAsync(string path, object body) => SendAsync(HttpMethod.Put, path, body);
    public Task<ApiResult> PostAsync(string path, object body) => SendAsync(HttpMethod.Post, path, body);
    public Task<ApiResult> DeleteAsync(string path) => SendAsync(HttpMethod.Delete, path, null);

    private async Task<ApiResult> SendAsync(HttpMethod method, string path, object? body)
    {
        try
        {
            using var request = new HttpRequestMessage(method, path);
            if (body != null)
                request.Content = JsonContent.Create(body);

            using var response = await _http.SendAsync(request);
            string text = await response.Content.ReadAsStringAsync();
            return new ApiResult((int)response.StatusCode, text);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return new ApiResult(0, ex.Message);
        }
    }

    /// <summary>Plain-English meaning of the status codes the Nuki spec documents for these endpoints.</summary>
    public static string Explain(int status) => status switch
    {
        0 => "No response (network error or timeout).",
        400 => "Bad request - the API rejected the request body or parameters.",
        401 => "Not authorized - the token is invalid, expired or revoked.",
        402 => "Account not paid - Nuki requires a paid subscription for this.",
        403 => "Forbidden - the token or account is not permitted to do this (check the token's permissions in Nuki Web).",
        404 => "Not found - wrong smartlock ID or authorization ID.",
        409 => "Parameter conflict - e.g. the PIN or name is already in use.",
        423 => "Locked - the lock is busy with another operation; try again shortly.",
        426 => "Account upgrade required - Nuki still restricts this for your account type.",
        _ when status >= 500 => "Nuki server error.",
        _ => ""
    };

    public void Dispose() => _http.Dispose();
}
