# Conector NTR-X — Exportação de Gravações/Transcrições

Pipeline de ingestão (V1): duas Lambdas .NET 8 que enumeram interações na API NTR-X e baixam
as transcrições para o S3 RAW, com estado/config dinâmica em DynamoDB e fila SQS entre elas.

- Contexto do projeto: [CONTEXTO.md](CONTEXTO.md)
- Plano de implementação: [docs/PLANO-IMPLEMENTACAO.md](docs/PLANO-IMPLEMENTACAO.md)
- Divisão entre pipeline de infra e de aplicação (o que cada uma cria, e por quê): [docs/PIPELINES.md](docs/PIPELINES.md)
- Especificação de infra (para a pipeline de infra): [docs/INFRA.md](docs/INFRA.md)

## Estrutura

```
src/
  Ntrx.Connector.Core/        Núcleo compartilhado: auth AAD, mTLS, cliente NTR-X,
                              parser do Search, estado/config (DynamoDB)
  Ntrx.Connector.Searcher/    Lambda agendada (EventBridge): enumera interações
                              (backfill/incremental) e publica na fila SQS
  Ntrx.Connector.Downloader/  Lambda consumidora do SQS: baixa transcrição + metadata
                              e grava no S3 RAW particionado por data
```

## Build

```bash
dotnet build NtrxConnector.sln -c Release
```

## Empacotar para deploy (zip por Lambda)

Requer a CLI `Amazon.Lambda.Tools` (`dotnet tool install -g Amazon.Lambda.Tools`):

```bash
cd src/Ntrx.Connector.Searcher   && dotnet lambda package -o ../../artifacts/ntrx-searcher.zip
cd src/Ntrx.Connector.Downloader && dotnet lambda package -o ../../artifacts/ntrx-downloader.zip
```

Deploy da aplicação (infra já provisionada conforme `docs/INFRA.md`):

```bash
aws lambda update-function-code --function-name ntrx-searcher   --zip-file fileb://artifacts/ntrx-searcher.zip
aws lambda update-function-code --function-name ntrx-downloader --zip-file fileb://artifacts/ntrx-downloader.zip
```

## Operação

- **Pausar/ajustar comportamento sem deploy**: editar o item `pk=CONFIG` na tabela
  `ntrx-connector-state` (flags de habilitação, tamanhos de janela, delays, zoneId).
- **Ajustar horários**: editar os schedules `ntrx-backfill` / `ntrx-incremental` no
  EventBridge Scheduler (timezone America/Sao_Paulo).
- **Reprocessar uma interação**: apagar o item `INT#<interactionId>` e reenviar a mensagem
  (ou aguardar o overlap do incremental/backfill reencontrá-la).
- **Falhas**: mensagens vão para a DLQ após 5 tentativas — inspecionar e usar redrive.

## Pontos a validar no ambiente do cliente (antes do primeiro run)

1. Schema real do response do `POST /Search/Interactions` — o parser
   ([SearchResultParser.cs](src/Ntrx.Connector.Core/Search/SearchResultParser.cs)) é tolerante,
   mas deve ser ajustado/fixado quando tivermos um response de exemplo.
2. Paginação do Search (a V1 assume que a janela de 24h retorna tudo em uma resposta —
   se houver paginação, reduzir `backfillWindowHours` no CONFIG ou implementar paging).
3. Rota de rede das Lambdas até o endpoint de token do AAD (login.microsoftonline.com) —
   além da rota até o host NTR-X interno.
