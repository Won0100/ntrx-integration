using Amazon.DynamoDBv2;
using Amazon.S3;
using Amazon.SecretsManager;
using Amazon.SQS;
using Ntrx.Connector.Core.Auth;
using Ntrx.Connector.Core.Http;
using Ntrx.Connector.Core.Secrets;
using Ntrx.Connector.Core.State;

namespace Ntrx.Connector.Core;

public sealed class ConnectorServices
{
    public required StateRepository State { get; init; }
    public required NtrxApiClient Api { get; init; }
    public required IAmazonSQS Sqs { get; init; }
    public required IAmazonS3 S3 { get; init; }
    public string? QueueUrl { get; init; }
    public string? RawBucket { get; init; }
}

/// <summary>
/// Inicialização compartilhada pelas duas Lambdas (cold start): carrega os DOIS secrets
/// (credenciais AAD + certificado mTLS) — Searcher e Downloader falam com a mesma API NTR-X.
/// </summary>
public static class ConnectorBootstrap
{
    public static async Task<ConnectorServices> CreateAsync()
    {
        var baseUrl = RequireEnv("NTRX_BASE_URL");
        var tableName = RequireEnv("STATE_TABLE");
        var aadSecretId = RequireEnv("AAD_SECRET_ID");
        var certSecretId = RequireEnv("CERT_SECRET_ID");

        using var secrets = new AmazonSecretsManagerClient();
        var aadCredentials = await SecretsLoader.LoadAsync<AadCredentials>(secrets, aadSecretId);
        var certificate = await SecretsLoader.LoadAsync<CertificateMaterial>(secrets, certSecretId);

        var tokenProvider = new AadTokenProvider(
            new HttpClient { Timeout = TimeSpan.FromSeconds(30) }, aadCredentials);

        return new ConnectorServices
        {
            State = new StateRepository(new AmazonDynamoDBClient(), tableName),
            Api = new NtrxApiClient(NtrxHttpClientFactory.Create(baseUrl, certificate), tokenProvider),
            Sqs = new AmazonSQSClient(),
            S3 = new AmazonS3Client(),
            QueueUrl = Environment.GetEnvironmentVariable("QUEUE_URL"),
            RawBucket = Environment.GetEnvironmentVariable("RAW_BUCKET"),
        };
    }

    private static string RequireEnv(string name) =>
        Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Variável de ambiente obrigatória ausente: {name}");
}
