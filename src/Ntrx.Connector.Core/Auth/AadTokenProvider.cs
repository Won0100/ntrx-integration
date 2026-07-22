using System.Text.Json;
using Ntrx.Connector.Core.Secrets;

namespace Ntrx.Connector.Core.Auth;

/// <summary>
/// Obtém e cacheia o token Bearer via client credentials na app do AD do cliente.
/// Renova automaticamente 5 minutos antes de expirar.
/// </summary>
public sealed class AadTokenProvider
{
    private readonly HttpClient _http;
    private readonly AadCredentials _credentials;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private string? _accessToken;
    private DateTimeOffset _expiresAtUtc;

    public AadTokenProvider(HttpClient http, AadCredentials credentials)
    {
        _http = http;
        _credentials = credentials;
    }

    public async Task<string> GetAccessTokenAsync(CancellationToken ct = default)
    {
        if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAtUtc)
            return _accessToken;

        await _gate.WaitAsync(ct);
        try
        {
            if (_accessToken is not null && DateTimeOffset.UtcNow < _expiresAtUtc)
                return _accessToken;

            using var form = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "client_credentials",
                ["client_id"] = _credentials.ClientId,
                ["client_secret"] = _credentials.ClientSecret,
                ["scope"] = _credentials.Scope,
            });

            using var response = await _http.PostAsync(_credentials.TokenEndpoint, form, ct);
            var payload = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException(
                    $"Falha ao obter token AAD ({(int)response.StatusCode}): {payload}");

            using var doc = JsonDocument.Parse(payload);
            _accessToken = doc.RootElement.GetProperty("access_token").GetString()
                           ?? throw new InvalidOperationException("Resposta do token sem access_token.");

            var expiresInSeconds = 3600;
            if (doc.RootElement.TryGetProperty("expires_in", out var expiresIn))
                expiresInSeconds = expiresIn.ValueKind == JsonValueKind.String
                    ? int.Parse(expiresIn.GetString()!)
                    : expiresIn.GetInt32();

            _expiresAtUtc = DateTimeOffset.UtcNow.AddSeconds(Math.Max(expiresInSeconds - 300, 60));
            return _accessToken;
        }
        finally
        {
            _gate.Release();
        }
    }
}
