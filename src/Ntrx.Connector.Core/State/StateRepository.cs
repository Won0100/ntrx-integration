using System.Globalization;
using Amazon.DynamoDBv2;
using Amazon.DynamoDBv2.Model;
using Ntrx.Connector.Core.Configuration;

namespace Ntrx.Connector.Core.State;

/// <summary>
/// Estado do conector no DynamoDB (tabela com partition key "pk"):
///   CONFIG                    — configuração dinâmica (ConnectorConfig)
///   CHECKPOINT#backfill       — última janela de backfill concluída
///   CHECKPOINT#incremental    — fim da última janela incremental
///   INT#{interactionId}       — status de download por interação (dedupe)
/// </summary>
public sealed class StateRepository
{
    private readonly IAmazonDynamoDB _dynamo;
    private readonly string _tableName;

    public StateRepository(IAmazonDynamoDB dynamo, string tableName)
    {
        _dynamo = dynamo;
        _tableName = tableName;
    }

    public async Task<ConnectorConfig> GetConfigAsync(CancellationToken ct = default)
    {
        var item = await GetItemAsync("CONFIG", ct);
        if (item is null)
            return new ConnectorConfig();

        var defaults = new ConnectorConfig();
        return new ConnectorConfig
        {
            Enabled = GetBool(item, "enabled", defaults.Enabled),
            BackfillEnabled = GetBool(item, "backfillEnabled", defaults.BackfillEnabled),
            IncrementalEnabled = GetBool(item, "incrementalEnabled", defaults.IncrementalEnabled),
            BackfillComplete = GetBool(item, "backfillComplete", false),
            BackfillStartUtc = GetDate(item, "backfillStartUtc") ?? defaults.BackfillStartUtc,
            BackfillWindowHours = GetInt(item, "backfillWindowHours", defaults.BackfillWindowHours),
            MaxWindowsPerRun = GetInt(item, "maxWindowsPerRun", defaults.MaxWindowsPerRun),
            IncrementalOverlapMinutes = GetInt(item, "incrementalOverlapMinutes", defaults.IncrementalOverlapMinutes),
            SearchDelaySeconds = GetInt(item, "searchDelaySeconds", defaults.SearchDelaySeconds),
            DownloadDelayMs = GetInt(item, "downloadDelayMs", defaults.DownloadDelayMs),
            ZoneId = item.TryGetValue("zoneId", out var zoneId) ? zoneId.S : null,
        };
    }

    public async Task<DateTimeOffset?> GetCheckpointAsync(string mode, CancellationToken ct = default)
    {
        var item = await GetItemAsync($"CHECKPOINT#{mode}", ct);
        return item is null ? null : GetDate(item, "value");
    }

    public Task SaveCheckpointAsync(string mode, DateTimeOffset value, CancellationToken ct = default) =>
        _dynamo.PutItemAsync(new PutItemRequest
        {
            TableName = _tableName,
            Item = new Dictionary<string, AttributeValue>
            {
                ["pk"] = new($"CHECKPOINT#{mode}"),
                ["value"] = new(value.ToUniversalTime().ToString("o")),
                ["updatedAtUtc"] = new(DateTimeOffset.UtcNow.ToString("o")),
            },
        }, ct);

    public Task SetBackfillCompleteAsync(CancellationToken ct = default) =>
        _dynamo.UpdateItemAsync(new UpdateItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["pk"] = new("CONFIG") },
            UpdateExpression = "SET backfillComplete = :true",
            ExpressionAttributeValues = new Dictionary<string, AttributeValue>
            {
                [":true"] = new() { BOOL = true },
            },
        }, ct);

    public async Task<string?> GetInteractionStatusAsync(Guid interactionId, CancellationToken ct = default)
    {
        var item = await GetItemAsync($"INT#{interactionId}", ct);
        return item is not null && item.TryGetValue("status", out var status) ? status.S : null;
    }

    public Task MarkInteractionAsync(Guid interactionId, string status, string? s3Key, CancellationToken ct = default)
    {
        var item = new Dictionary<string, AttributeValue>
        {
            ["pk"] = new($"INT#{interactionId}"),
            ["status"] = new(status),
            ["updatedAtUtc"] = new(DateTimeOffset.UtcNow.ToString("o")),
        };
        if (s3Key is not null)
            item["s3Key"] = new(s3Key);

        return _dynamo.PutItemAsync(new PutItemRequest { TableName = _tableName, Item = item }, ct);
    }

    private async Task<Dictionary<string, AttributeValue>?> GetItemAsync(string pk, CancellationToken ct)
    {
        var response = await _dynamo.GetItemAsync(new GetItemRequest
        {
            TableName = _tableName,
            Key = new Dictionary<string, AttributeValue> { ["pk"] = new(pk) },
            ConsistentRead = true,
        }, ct);
        return response.IsItemSet ? response.Item : null;
    }

    private static bool GetBool(Dictionary<string, AttributeValue> item, string name, bool fallback) =>
        item.TryGetValue(name, out var value) && value.IsBOOLSet ? value.BOOL : fallback;

    private static int GetInt(Dictionary<string, AttributeValue> item, string name, int fallback) =>
        item.TryGetValue(name, out var value) && value.N is not null
            ? int.Parse(value.N, CultureInfo.InvariantCulture)
            : fallback;

    private static DateTimeOffset? GetDate(Dictionary<string, AttributeValue> item, string name) =>
        item.TryGetValue(name, out var value) && value.S is not null
           && DateTimeOffset.TryParse(value.S, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed
            : null;
}
