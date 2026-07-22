using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.Lambda.SQSEvents;
using Amazon.S3.Model;
using Ntrx.Connector.Core;
using Ntrx.Connector.Core.Configuration;
using Ntrx.Connector.Core.Models;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Ntrx.Connector.Downloader;

public class Function
{
    private static readonly Task<ConnectorServices> Bootstrap = ConnectorBootstrap.CreateAsync();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// Consome a fila de download. Requer ReportBatchItemFailures habilitado no event source
    /// mapping: só as mensagens que falharam voltam para a fila (e vão à DLQ após N tentativas).
    /// </summary>
    public async Task<SQSBatchResponse> Handler(SQSEvent sqsEvent, ILambdaContext context)
    {
        var services = await Bootstrap;
        if (services.RawBucket is null)
            throw new InvalidOperationException("Variável de ambiente RAW_BUCKET não configurada.");

        var config = await services.State.GetConfigAsync();
        var failures = new List<SQSBatchResponse.BatchItemFailure>();

        foreach (var record in sqsEvent.Records)
        {
            try
            {
                await ProcessRecordAsync(services, config, record, context);
            }
            catch (Exception ex)
            {
                context.Logger.LogError($"Falha na mensagem {record.MessageId}: {ex}");
                failures.Add(new SQSBatchResponse.BatchItemFailure { ItemIdentifier = record.MessageId });
            }

            // Pacing conservador entre downloads (config dinâmica).
            if (config.DownloadDelayMs > 0)
                await Task.Delay(config.DownloadDelayMs);
        }

        return new SQSBatchResponse { BatchItemFailures = failures };
    }

    private static async Task ProcessRecordAsync(
        ConnectorServices services, ConnectorConfig config, SQSEvent.SQSMessage record, ILambdaContext context)
    {
        var message = JsonSerializer.Deserialize<TranscriptDownloadMessage>(record.Body, JsonOptions)
                      ?? throw new InvalidOperationException("Corpo da mensagem SQS vazio/inválido.");

        var status = await services.State.GetInteractionStatusAsync(message.InteractionId);
        if (status is "downloaded" or "no_transcript")
        {
            context.Logger.LogInformation($"{message.InteractionId}: já processada ({status}), ignorando.");
            return;
        }

        var zoneId = config.ZoneId
                     ?? throw new InvalidOperationException("zoneId ausente no item CONFIG do DynamoDB.");

        var transcript = await services.Api.GetTranscriptAsync(message.InteractionId, zoneId);

        var startTimeUtc = (message.StartTimeUtc ?? DateTimeOffset.UtcNow).ToUniversalTime();
        var prefix = $"raw/transcripts/year={startTimeUtc:yyyy}/month={startTimeUtc:MM}/day={startTimeUtc:dd}";

        if (transcript is null)
        {
            await services.State.MarkInteractionAsync(message.InteractionId, "no_transcript", null);
            context.Logger.LogWarning($"{message.InteractionId}: transcrição indisponível (404).");
            return;
        }

        var key = $"{prefix}/{message.InteractionId}{ExtensionFor(transcript.ContentType)}";
        await PutObjectAsync(services, key, transcript.Body, transcript.ContentType);

        if (message.InteractionJson is not null)
            await PutObjectAsync(services, $"{prefix}/{message.InteractionId}.meta.json",
                message.InteractionJson, "application/json");

        await services.State.MarkInteractionAsync(message.InteractionId, "downloaded", key);
        context.Logger.LogInformation($"{message.InteractionId}: gravada em s3://{services.RawBucket}/{key}");
    }

    private static Task PutObjectAsync(ConnectorServices services, string key, string content, string contentType) =>
        services.S3.PutObjectAsync(new PutObjectRequest
        {
            BucketName = services.RawBucket,
            Key = key,
            ContentBody = content,
            ContentType = contentType,
        });

    private static string ExtensionFor(string contentType) => contentType switch
    {
        "text/vtt" => ".vtt",
        "text/eml" => ".eml",
        _ => ".json",
    };
}
