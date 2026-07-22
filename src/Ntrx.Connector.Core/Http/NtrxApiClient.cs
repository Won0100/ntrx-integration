using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Ntrx.Connector.Core.Auth;
using Ntrx.Connector.Core.Search;
using Ntrx.Connector.Core.Secrets;

namespace Ntrx.Connector.Core.Http;

public sealed record TranscriptContent(string Body, string ContentType);

/// <summary>Erro retornado pela API NTR-X (não-transporte, não-retryável).</summary>
public sealed class NtrxApiException : Exception
{
    public int StatusCode { get; }

    public NtrxApiException(int statusCode, string message) : base(message)
        => StatusCode = statusCode;
}

public static class NtrxHttpClientFactory
{
    /// <summary>HttpClient com o certificado de cliente (mTLS) carregado do Secrets Manager.</summary>
    public static HttpClient Create(string baseUrl, CertificateMaterial certificate)
    {
        var handler = new HttpClientHandler();
        var pfxBytes = Convert.FromBase64String(certificate.PfxBase64);
        handler.ClientCertificates.Add(
            new X509Certificate2(pfxBytes, certificate.Password, X509KeyStorageFlags.EphemeralKeySet));

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(baseUrl.TrimEnd('/') + "/"),
            // O Search do NTR-X leva 15s+ para responder; margem generosa.
            Timeout = TimeSpan.FromSeconds(120),
        };

        if (!string.IsNullOrEmpty(certificate.ExtraHeaderName))
            client.DefaultRequestHeaders.TryAddWithoutValidation(
                certificate.ExtraHeaderName, certificate.ExtraHeaderValue);

        return client;
    }
}

public sealed class NtrxApiClient
{
    private const int MaxAttempts = 4;

    private readonly HttpClient _http;
    private readonly AadTokenProvider _tokens;

    public NtrxApiClient(HttpClient http, AadTokenProvider tokens)
    {
        _http = http;
        _tokens = tokens;
    }

    public async Task<IReadOnlyList<InteractionSummary>> SearchInteractionsAsync(
        DateTimeOffset fromUtc, DateTimeOffset toUtc, CancellationToken ct = default)
    {
        var body = JsonSerializer.Serialize(new
        {
            QueryCondition = new
            {
                Combinator = "And",
                QueryFilters = new object[]
                {
                    new
                    {
                        Column = "StartTime",
                        Operation = "Between",
                        Value = new[] { FormatUtc(fromUtc), FormatUtc(toUtc) },
                    },
                },
            },
        });

        using var response = await SendWithRetryAsync(() =>
            new HttpRequestMessage(HttpMethod.Post, "Search/Interactions")
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            }, ct);

        var json = await response.Content.ReadAsStringAsync(ct);
        return SearchResultParser.Parse(json);
    }

    /// <summary>Retorna null quando a transcrição não existe (404).</summary>
    public async Task<TranscriptContent?> GetTranscriptAsync(
        Guid interactionId, string zoneId, CancellationToken ct = default)
    {
        using var response = await SendWithRetryAsync(() =>
        {
            var request = new HttpRequestMessage(HttpMethod.Get,
                $"Search/Interactions/{interactionId}/Transcript?zoneId={Uri.EscapeDataString(zoneId)}");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            return request;
        }, ct, allowNotFound: true);

        if (response.StatusCode == HttpStatusCode.NotFound)
            return null;

        var content = await response.Content.ReadAsStringAsync(ct);
        var contentType = response.Content.Headers.ContentType?.MediaType ?? "application/json";
        return new TranscriptContent(content, contentType);
    }

    private async Task<HttpResponseMessage> SendWithRetryAsync(
        Func<HttpRequestMessage> requestFactory, CancellationToken ct, bool allowNotFound = false)
    {
        for (var attempt = 1; ; attempt++)
        {
            HttpResponseMessage? response = null;
            try
            {
                using var request = requestFactory();
                request.Headers.Authorization =
                    new AuthenticationHeaderValue("Bearer", await _tokens.GetAccessTokenAsync(ct));

                response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);

                if (response.IsSuccessStatusCode)
                    return response;
                if (allowNotFound && response.StatusCode == HttpStatusCode.NotFound)
                    return response;

                var retryable = response.StatusCode == HttpStatusCode.TooManyRequests
                                || (int)response.StatusCode >= 500;
                if (!retryable || attempt == MaxAttempts)
                {
                    var error = await response.Content.ReadAsStringAsync(ct);
                    var status = (int)response.StatusCode;
                    response.Dispose();
                    throw new NtrxApiException(status, $"NTR-X retornou {status}: {Truncate(error)}");
                }

                var delay = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(5 * attempt);
                response.Dispose();
                await Task.Delay(delay, ct);
            }
            catch (Exception ex) when (attempt < MaxAttempts && ex is HttpRequestException or TaskCanceledException)
            {
                response?.Dispose();
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt), ct);
            }
        }
    }

    private static string FormatUtc(DateTimeOffset value) =>
        value.ToUniversalTime().ToString("yyyy-MM-dd'T'HH:mm:ss+00:00");

    private static string Truncate(string value) =>
        value.Length <= 2000 ? value : value[..2000] + "…";
}
