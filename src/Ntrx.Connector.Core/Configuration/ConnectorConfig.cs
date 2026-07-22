namespace Ntrx.Connector.Core.Configuration;

/// <summary>
/// Configuração dinâmica do conector, lida do item CONFIG no DynamoDB a cada execução.
/// Alterar o item muda o comportamento sem redeploy.
/// </summary>
public sealed class ConnectorConfig
{
    public bool Enabled { get; init; } = true;
    public bool BackfillEnabled { get; init; } = true;
    public bool IncrementalEnabled { get; init; } = true;
    public bool BackfillComplete { get; init; }

    public DateTimeOffset BackfillStartUtc { get; init; } = new(2023, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>Tamanho da janela de busca do backfill, em horas.</summary>
    public int BackfillWindowHours { get; init; } = 24;

    /// <summary>Máximo de janelas de backfill processadas por invocação do Searcher.</summary>
    public int MaxWindowsPerRun { get; init; } = 20;

    /// <summary>Sobreposição da janela incremental para não perder ligações na borda (dedupe cobre as repetidas).</summary>
    public int IncrementalOverlapMinutes { get; init; } = 5;

    /// <summary>Pausa entre chamadas de Search consecutivas (pacing conservador).</summary>
    public int SearchDelaySeconds { get; init; } = 5;

    /// <summary>Pausa entre downloads de transcrição consecutivos.</summary>
    public int DownloadDelayMs { get; init; } = 1000;

    /// <summary>Zona NTR-X usada no download de transcrições (GUID). Obrigatório para o Downloader.</summary>
    public string? ZoneId { get; init; }
}
