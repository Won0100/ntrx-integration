using System.Text.Json;
using Amazon.Lambda.Core;
using Amazon.SQS.Model;
using Ntrx.Connector.Core;
using Ntrx.Connector.Core.Configuration;
using Ntrx.Connector.Core.Models;
using Ntrx.Connector.Core.Search;

[assembly: LambdaSerializer(typeof(Amazon.Lambda.Serialization.SystemTextJson.DefaultLambdaJsonSerializer))]

namespace Ntrx.Connector.Searcher;

/// <summary>Payload do EventBridge Scheduler: {"mode":"backfill"} ou {"mode":"incremental"}.</summary>
public class SearcherInput
{
    public string? Mode { get; set; }
}

public class Function
{
    /// <summary>Mensagens acima disso vão sem o JSON da interação (limite SQS 256KB por batch).</summary>
    private const int MaxInteractionJsonLength = 60_000;

    private static readonly Task<ConnectorServices> Bootstrap = ConnectorBootstrap.CreateAsync();
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<string> Handler(SearcherInput? input, ILambdaContext context)
    {
        var services = await Bootstrap;
        if (services.QueueUrl is null)
            throw new InvalidOperationException("Variável de ambiente QUEUE_URL não configurada.");

        var config = await services.State.GetConfigAsync();
        if (!config.Enabled)
        {
            context.Logger.LogInformation("Conector pausado (CONFIG.enabled=false). Nada a fazer.");
            return "disabled";
        }

        var mode = input?.Mode?.Trim().ToLowerInvariant() ?? "incremental";
        return mode switch
        {
            "backfill" => await RunBackfillAsync(services, config, context),
            "incremental" => await RunIncrementalAsync(services, config, context),
            _ => throw new ArgumentException($"Modo desconhecido: '{mode}'. Use 'backfill' ou 'incremental'."),
        };
    }

    private static async Task<string> RunBackfillAsync(
        ConnectorServices services, ConnectorConfig config, ILambdaContext context)
    {
        if (config.BackfillComplete)
        {
            context.Logger.LogInformation("Backfill já completo — desabilite o schedule ntrx-backfill.");
            return "backfill-complete";
        }
        if (!config.BackfillEnabled)
            return "backfill-disabled";

        var checkpoint = await services.State.GetCheckpointAsync("backfill") ?? config.BackfillStartUtc;
        var windows = 0;
        var enqueued = 0;

        // Processa janelas até estourar o limite por execução ou faltar tempo (margem p/ concluir
        // a janela corrente: Search leva 15s+ e o envio ao SQS também consome tempo).
        while (windows < config.MaxWindowsPerRun && context.RemainingTime > TimeSpan.FromMinutes(3))
        {
            var now = DateTimeOffset.UtcNow;
            if (checkpoint >= now.AddMinutes(-5))
            {
                await services.State.SetBackfillCompleteAsync();
                context.Logger.LogInformation(
                    "Backfill alcançou o presente — marcado como completo. Desabilite o schedule ntrx-backfill e confira o schedule incremental.");
                return $"backfill-complete windows={windows} enqueued={enqueued}";
            }

            var from = checkpoint;
            var to = from.AddHours(config.BackfillWindowHours);
            if (to > now)
                to = now;

            var interactions = await services.Api.SearchInteractionsAsync(from, to);
            enqueued += await EnqueueAsync(services, interactions, "backfill");

            checkpoint = to;
            await services.State.SaveCheckpointAsync("backfill", checkpoint);
            windows++;
            context.Logger.LogInformation(
                $"Backfill {from:yyyy-MM-dd HH:mm} → {to:yyyy-MM-dd HH:mm}: {interactions.Count} interações (execução: {enqueued} enfileiradas).");

            if (config.SearchDelaySeconds > 0)
                await Task.Delay(TimeSpan.FromSeconds(config.SearchDelaySeconds));
        }

        return $"backfill-progress checkpoint={checkpoint:o} windows={windows} enqueued={enqueued}";
    }

    private static async Task<string> RunIncrementalAsync(
        ConnectorServices services, ConnectorConfig config, ILambdaContext context)
    {
        if (!config.IncrementalEnabled)
            return "incremental-disabled";

        var now = DateTimeOffset.UtcNow;
        var checkpoint = await services.State.GetCheckpointAsync("incremental") ?? now.AddMinutes(-10);
        var from = checkpoint.AddMinutes(-config.IncrementalOverlapMinutes);

        // Se o incremental ficou parado muito tempo, não tenta cobrir o buraco numa chamada só —
        // o período faltante deve ser coberto reativando o backfill com a janela adequada.
        if (from < now.AddHours(-24))
        {
            context.Logger.LogWarning(
                $"Checkpoint incremental antigo ({from:o}); limitando a janela às últimas 24h. " +
                "Cubra o período faltante via backfill.");
            from = now.AddHours(-24);
        }

        var interactions = await services.Api.SearchInteractionsAsync(from, now);
        var enqueued = await EnqueueAsync(services, interactions, "incremental");
        await services.State.SaveCheckpointAsync("incremental", now);

        context.Logger.LogInformation(
            $"Incremental {from:HH:mm:ss} → {now:HH:mm:ss}: {interactions.Count} interações, {enqueued} enfileiradas.");
        return $"incremental found={interactions.Count} enqueued={enqueued}";
    }

    private static async Task<int> EnqueueAsync(
        ConnectorServices services, IReadOnlyList<InteractionSummary> interactions, string mode)
    {
        var entries = new List<SendMessageBatchRequestEntry>(10);
        var sent = 0;

        foreach (var interaction in interactions)
        {
            var message = new TranscriptDownloadMessage
            {
                InteractionId = interaction.InteractionId,
                StartTimeUtc = interaction.StartTimeUtc,
                Mode = mode,
                InteractionJson = interaction.RawJson.Length <= MaxInteractionJsonLength
                    ? interaction.RawJson
                    : null,
            };
            entries.Add(new SendMessageBatchRequestEntry(
                entries.Count.ToString(), JsonSerializer.Serialize(message, JsonOptions)));

            if (entries.Count == 10)
                sent += await FlushAsync(services, entries);
        }

        if (entries.Count > 0)
            sent += await FlushAsync(services, entries);
        return sent;
    }

    private static async Task<int> FlushAsync(
        ConnectorServices services, List<SendMessageBatchRequestEntry> entries)
    {
        var response = await services.Sqs.SendMessageBatchAsync(services.QueueUrl, entries);
        if (response.Failed.Count > 0)
            throw new InvalidOperationException(
                $"{response.Failed.Count} mensagens falharam no envio ao SQS: {response.Failed[0].Message}");

        var count = entries.Count;
        entries.Clear();
        return count;
    }
}
