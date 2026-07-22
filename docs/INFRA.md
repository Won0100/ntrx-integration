# Especificação de Infra — Conector NTR-X (V1)

Documento para a **pipeline de infra** provisionar os recursos que cercam as Lambdas.
**As funções Lambda em si (criação, atualização, triggers) não fazem parte deste
documento** — são de responsabilidade exclusiva da pipeline de aplicação. Veja a divisão
completa e o porquê em [`docs/PIPELINES.md`](PIPELINES.md).

Nomes são sugestões — se mudarem, ajustar apenas os parâmetros publicados no SSM
Parameter Store (§6), que é o contrato entre as duas pipelines.

## 1. Rede
- As duas Lambdas rodam **dentro da VPC** com rota para a rede interna do cliente
  (Direct Connect/VPN) — precisam alcançar o host da API NTR-X (HTTPS/443, mTLS).
- Subnets privadas (≥2 AZs) + Security Group com egress 443 para o CIDR/host do NTR-X
  e para os serviços AWS.
- **VPC Endpoints** (recomendado, já que as subnets são privadas): S3 (gateway),
  DynamoDB (gateway), SQS, Secrets Manager, CloudWatch Logs (interface).
- **Egress para internet pública (NAT Gateway ou rota equivalente já existente do
  cliente)**: a troca do token client credentials acontece contra
  `login.microsoftonline.com` (endpoint público da Microsoft) — **caminho de rede
  diferente** do Direct Connect usado para chegar no host do NTR-X. Sem esse egress, a
  Lambda trava com timeout ao buscar o token, mesmo com o Direct Connect perfeito.
  Confirmar com o time de rede do cliente como o tráfego para AAD já sai hoje (pode já
  existir uma rota corporativa para isso, sem precisar de NAT Gateway dedicado).

## 2. Secrets Manager
| Secret | Conteúdo (JSON) |
|---|---|
| `ntrx/aad-credentials` | `{ "tokenEndpoint": "https://login.microsoftonline.com/<tenantId>/oauth2/v2.0/token", "clientId": "...", "clientSecret": "...", "scope": "<api-scope>/.default" }` |
| `ntrx/mtls-certificate` | `{ "pfxBase64": "<PFX em base64>", "password": "<senha do PFX>", "extraHeaderName": null, "extraHeaderValue": null }` |

`extraHeaderName/Value`: opcional — se a API exigir um header adicional com o secret do
certificado, preencher aqui (a aplicação envia automaticamente quando presente).
**Ambas as Lambdas leem os dois secrets.** Criptografar com KMS (CMK dedicada opcional).

## 3. DynamoDB
- Tabela `ntrx-connector-state`
  - Partition key: `pk` (String). Sem sort key. Billing: on-demand.
  - Itens usados pela aplicação: `CONFIG`, `CHECKPOINT#backfill`, `CHECKPOINT#incremental`,
    `INT#<interactionId>` (dedupe/status de download).
  - TTL opcional no atributo `ttl` (para expirar itens `INT#` após ex. 90 dias, se desejado).
- Após criar, inserir o item de configuração inicial:
```json
{
  "pk": {"S": "CONFIG"},
  "enabled": {"BOOL": true},
  "backfillEnabled": {"BOOL": true},
  "incrementalEnabled": {"BOOL": true},
  "backfillStartUtc": {"S": "2023-01-01T00:00:00Z"},
  "backfillWindowHours": {"N": "24"},
  "maxWindowsPerRun": {"N": "20"},
  "incrementalOverlapMinutes": {"N": "5"},
  "searchDelaySeconds": {"N": "5"},
  "downloadDelayMs": {"N": "1000"},
  "zoneId": {"S": "<GUID da zona NTR-X>"}
}
```

## 4. SQS
| Fila | Config |
|---|---|
| `ntrx-transcript-download` (standard) | Visibility timeout **360s**; redrive → DLQ após **5** recebimentos |
| `ntrx-transcript-download-dlq` | Retenção 14 dias |

## 5. S3
- Bucket `ntrx-raw-<conta>-<região>` (nome globalmente único):
  SSE-KMS, Block Public Access total, versionamento ligado, acesso restrito à squad
  + roles das Lambdas. Prefixo usado pela app: `raw/transcripts/...`.

## 6. IAM (uma role de execução por Lambda — a role existe, a função ainda não)
A pipeline de infra cria as **roles** (com trust policy permitindo `lambda.amazonaws.com`
assumi-las) e suas políticas. A pipeline de aplicação, ao criar a função, apenas
**referencia o ARN da role já existente** — não precisa (e não deve) ter permissão para
criar/alterar roles IAM. Isso mantém a separação de responsabilidades sem exigir que a
função já exista.

**Role `ntrx-searcher-role`:**
- `AWSLambdaVPCAccessExecutionRole` (managed) — ENI + logs
- `secretsmanager:GetSecretValue` nos 2 secrets (+ `kms:Decrypt` na CMK dos secrets)
- `dynamodb:GetItem,PutItem,UpdateItem` na tabela `ntrx-connector-state`
- `sqs:SendMessage` na fila `ntrx-transcript-download`

**Role `ntrx-downloader-role`:**
- `AWSLambdaVPCAccessExecutionRole` (managed)
- `secretsmanager:GetSecretValue` nos 2 secrets (+ `kms:Decrypt`)
- `dynamodb:GetItem,PutItem` na tabela `ntrx-connector-state`
- `sqs:ReceiveMessage,DeleteMessage,GetQueueAttributes` na fila
- `s3:PutObject` em `arn:aws:s3:::<bucket-raw>/raw/*` (+ `kms:GenerateDataKey` na CMK do bucket)

**Role `ntrx-scheduler-invoke-role`** (assumida pelo EventBridge Scheduler, não pela
Lambda): `lambda:InvokeFunction` restrito aos ARNs `ntrx-searcher` e `ntrx-searcher:*`
(inclui versões/aliases). Como o ARN da Lambda é previsível
(`arn:aws:lambda:<região>:<conta>:function:ntrx-searcher`), essa política pode ser criada
**antes** de a função existir — é só uma string de recurso, IAM não valida existência.

## 7. SSM Parameter Store — contrato de handoff infra → aplicação
Depois de provisionar os recursos acima, a pipeline de infra publica os identificadores
sob o prefixo `/ntrx-connector/` para a pipeline de aplicação consumir em tempo de deploy
(sem nome de recurso hardcoded em nenhuma das duas pipelines):

| Parâmetro | Valor |
|---|---|
| `/ntrx-connector/network/subnet-ids` | subnets privadas (StringList) |
| `/ntrx-connector/network/security-group-id` | Security Group das Lambdas |
| `/ntrx-connector/iam/searcher-role-arn` | ARN da `ntrx-searcher-role` |
| `/ntrx-connector/iam/downloader-role-arn` | ARN da `ntrx-downloader-role` |
| `/ntrx-connector/iam/scheduler-role-arn` | ARN da `ntrx-scheduler-invoke-role` |
| `/ntrx-connector/state/table-name` | nome da tabela DynamoDB |
| `/ntrx-connector/queue/url` | URL da fila `ntrx-transcript-download` |
| `/ntrx-connector/queue/arn` | ARN da mesma fila (para o event source mapping) |
| `/ntrx-connector/s3/raw-bucket-name` | nome do bucket RAW |
| `/ntrx-connector/secrets/aad-secret-id` | `ntrx/aad-credentials` |
| `/ntrx-connector/secrets/cert-secret-id` | `ntrx/mtls-certificate` |
| `/ntrx-connector/api/base-url` | `https://<host-ntrx>/public/api/v1` |

## 8. Observabilidade (mínimo V1)
- Log groups das 2 Lambdas — infra define **apenas a política de retenção** (90 dias);
  os log groups em si são criados automaticamente pela Lambda no primeiro invoke, ou pela
  pipeline de aplicação junto com a função (§ na `docs/PIPELINES.md`).
- Alarmes: `ApproximateNumberOfMessagesVisible` da DLQ > 0; Errors > 0 em cada Lambda
  (métrica por nome de função — pode ser criado por infra referenciando o nome, mesmo
  antes de a função existir).
- CloudTrail já cobre auditoria de acesso a S3/KMS/Secrets (requisito do PM).
