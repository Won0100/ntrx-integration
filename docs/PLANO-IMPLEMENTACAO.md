# Plano de Implementação — Conector NTR-X

## Visão geral da arquitetura

```
EventBridge Scheduler ──▶ Lambda Searcher (C#) ──▶ SQS work queue ──▶ Lambda Downloader (C#) ──▶ S3 RAW
  2 schedules:             POST /Search/Interactions     │              GET /Transcript             (SSE-KMS)
  - backfill (fora do      janela por dia (backfill)     └─▶ DLQ        + escrita metadata
    horário de mercado)    ou últimos N min (increm.)
  - incremental (5/5min                                  ┌──────────────────────────────┐
    no horário de mercado) │  DynamoDB: config dinâmica, │
                           │  checkpoints, dedupe        │
                           └──────────────────────────────┘
  ★ AMBAS as Lambdas usam: Secrets Manager (credenciais AAD p/ token Bearer
    + certificado mTLS PFX) — as duas falam com a mesma API NTR-X.
```

## Fases

### Fase 0 — Pré-requisitos (infra do cliente, antes de qualquer deploy)
- [ ] Conectividade AWS ↔ rede interna do cliente validada (VPC + Direct Connect/VPN);
      resolver DNS do host NTR-X de dentro da VPC.
- [ ] App AAD com client credentials funcionando (já existe) → cadastrar no Secrets Manager.
- [ ] Certificado mTLS exportado como PFX → cadastrar no Secrets Manager (base64 + senha).
- [ ] Confirmar com a NICE: formato de paginação do `POST /Search/Interactions`
      (tamanho de página, campo de continuação) e schema do response.
- [ ] Levantar volume médio de ligações/dia (dimensionamento fino do incremental).

### Fase 1 — V1: pipeline de ingestão até o S3 RAW  ← **(esta entrega)**
Aplicação C# (.NET 8) com duas Lambdas + core compartilhado:
- **Searcher**: enumera interações por janela de tempo e publica na fila SQS.
  - Modo `backfill`: avança janelas de 24h a partir do checkpoint (início 01/01/2023),
    processa várias janelas por invocação até ~3 min antes do timeout, salva checkpoint
    por janela (retomável). Quando alcançar "agora", marca backfill como completo.
  - Modo `incremental`: busca `[checkpoint − overlap, agora]` (overlap 5 min p/ não perder
    borda; dedupe resolve duplicata), salva checkpoint.
  - O modo vem no **payload do schedule** (`{"mode":"backfill"}` / `{"mode":"incremental"}`)
    → uma única Lambda, dois schedules.
- **Downloader**: consome SQS (batch, partial batch response), dedupe via DynamoDB,
  baixa transcrição (`GET /Search/Interactions/{id}/Transcript?zoneId=...`), grava no S3:
  - `raw/transcripts/year=YYYY/month=MM/day=DD/{interactionId}.json` (transcrição)
  - `raw/transcripts/year=.../{interactionId}.meta.json` (metadata da interação vinda do
    Search — insumo da curadoria p/ identificar a área)
  - pacing configurável entre downloads (default 1s — conservador).
- **Config dinâmica em DynamoDB** (item `CONFIG`): habilitar/pausar cada modo, tamanho de
  janela, overlap, delays, zoneId. Alterar comportamento = editar o item, **sem redeploy**.
  Horários de execução = editar os schedules do EventBridge (console/CLI), também sem deploy.

### Fase 2 — Curadoria e democratização
- Job de curadoria (Glue ou Lambda) lendo o RAW: identificação de área
  → confiança OK: S3 Curated `area=<área>/year=/month=/day=/` → Glue Data Catalog
  → confiança baixa: S3 Quarantine.
- Lake Formation: grants por área (Asset só Asset, Corretora só Corretora) → Athena/BI.

### Fase 3 — Hardening
- Alarmes CloudWatch (profundidade da DLQ, erros das Lambdas, idade do checkpoint).
- Auto-desabilitar schedule de backfill ao completar (chamada `UpdateSchedule` pela Lambda).
- Paginação real do Search conforme resposta da NICE; download de áudio (grupo Downloads)
  se entrar no escopo; dashboards.

## Separação infra × aplicação (modelo de deploy no cliente)

| | Quem cria | Como |
|---|---|---|
| Infra (rede, S3, SQS, DynamoDB, Secrets, IAM roles) | Pipeline de infra | Conforme `docs/INFRA.md` |
| Aplicação (as 2 funções Lambda + seus triggers) | Pipeline de aplicação | `dotnet lambda deploy-function` (create-or-update) + criação do event source mapping e dos schedules do EventBridge Scheduler no mesmo deploy |

**A Lambda em si — criação, atualização e triggers — é sempre da pipeline de
aplicação, nunca da infra**, incluindo o primeiro deploy (não há etapa de "criar função
vazia" na infra). Ver a divisão completa, o contrato via SSM Parameter Store e a
justificativa técnica em [`docs/PIPELINES.md`](PIPELINES.md).

## Backfill — estimativa de execução
- ~1.290 janelas de 1 dia; Search ~15–30s por janela → **~6–11h só de enumeração**,
  distribuídas nas janelas noturnas (18h–08h) → termina em 1–2 noites.
- Downloads a ~1/s ao longo da noite (~50k/noite). Se o volume total for maior, aumentar
  o pacing (config dinâmica) ou a concorrência do Downloader gradualmente — o limite duro
  é 100 requests concorrentes na API.
