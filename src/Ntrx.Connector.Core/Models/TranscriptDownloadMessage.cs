namespace Ntrx.Connector.Core.Models;

/// <summary>Mensagem publicada pelo Searcher na fila SQS e consumida pelo Downloader.</summary>
public sealed class TranscriptDownloadMessage
{
    public Guid InteractionId { get; set; }

    public DateTimeOffset? StartTimeUtc { get; set; }

    /// <summary>"backfill" ou "incremental" (rastreabilidade nos logs).</summary>
    public string Mode { get; set; } = string.Empty;

    /// <summary>
    /// JSON bruto da interação vindo do Search — gravado como .meta.json no S3, insumo da
    /// curadoria para identificar a área. Omitido se exceder o limite de tamanho da mensagem.
    /// </summary>
    public string? InteractionJson { get; set; }
}
