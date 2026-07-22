using System.Text.Json;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;

namespace Ntrx.Connector.Core.Secrets;

/// <summary>Credenciais da app registrada no AD do cliente (client credentials flow).</summary>
public sealed record AadCredentials(
    string TokenEndpoint,
    string ClientId,
    string ClientSecret,
    string Scope);

/// <summary>
/// Certificado de cliente para o mTLS com a API NTR-X.
/// <see cref="ExtraHeaderName"/>/<see cref="ExtraHeaderValue"/> são opcionais — usados se a API
/// exigir um header adicional com o secret do certificado.
/// </summary>
public sealed record CertificateMaterial(
    string PfxBase64,
    string? Password,
    string? ExtraHeaderName,
    string? ExtraHeaderValue);

public static class SecretsLoader
{
    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web);

    public static async Task<T> LoadAsync<T>(IAmazonSecretsManager client, string secretId, CancellationToken ct = default)
    {
        var response = await client.GetSecretValueAsync(new GetSecretValueRequest { SecretId = secretId }, ct);
        return JsonSerializer.Deserialize<T>(response.SecretString, Options)
               ?? throw new InvalidOperationException($"Secret '{secretId}' vazio ou em formato inválido.");
    }
}
