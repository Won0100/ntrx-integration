# Duas Pipelines — Infra e Aplicação

O cliente separa infra e aplicação em pipelines distintas. Este documento define
exatamente o que cada uma cria, na ordem, e o contrato entre elas.

**Regra central: a Lambda (criação, atualização e triggers) é 100% da pipeline de
aplicação.** A pipeline de infra nunca declara a função Lambda — nem mesmo uma versão
vazia dela. Ela cria tudo o que existe *ao redor* da função (rede, dados, mensageria,
secrets, roles IAM) e publica os identificadores no SSM Parameter Store
(contrato em [`INFRA.md §7`](INFRA.md)). Isso não é uma simplificação por preguiça — é a
opção tecnicamente mais correta pelos motivos abaixo.

## Por que não criar a Lambda "em branco" na infra e só completar o código na aplicação?

Esse padrão (infra cria a função com um zip placeholder; aplicação faz
`update-function-code` depois) existe em alguns times, mas só se justifica em um cenário
específico: quando a **ferramenta de IaC da infra** (Terraform/CloudFormation) precisa
*declarar* o recurso `AWS::Lambda::Function` e essas ferramentas exigem algum artefato de
código no momento da criação — daí o placeholder. **Esse não é o nosso caso**: a pipeline
de aplicação vai gerenciar a função de forma imperativa (`dotnet lambda deploy-function`,
que já faz *create-or-update*: cria na primeira execução, atualiza código+configuração
nas seguintes). Não há uma segunda ferramenta de IaC disputando a posse do recurso.

Se ainda assim a infra declarasse a função (mesmo vazia) em Terraform/CloudFormation,
isso criaria um problema real: a cada apply da infra, a ferramenta compararia o estado
declarado (placeholder) com o estado real (código publicado pela aplicação) e entraria em
conflito — na melhor hipótese um diff incômodo, na pior uma reversão do código para o
placeholder. Evitar que a infra declare a função elimina esse risco de raiz.

Duas dependências técnicas reais (não é possível contornar, mas nenhuma exige criar a
função antes):
1. **`sqs:CreateEventSourceMapping` exige que a função já exista** (é uma chamada do
   plano de controle do Lambda, não uma referência genérica de ARN) → por isso o event
   source mapping é criado pela própria pipeline de aplicação, depois de criar/atualizar
   a função, no mesmo passo de deploy.
2. O ARN da Lambda é **prev­isível** (`arn:aws:lambda:<região>:<conta>:function:<nome>`),
   então políticas IAM e o *target* do EventBridge Scheduler podem referenciá-lo mesmo
   antes de a função existir — por isso a role do Scheduler pode ser criada pela infra
   sem esperar o primeiro deploy da aplicação.

## O que a pipeline de infra cria

Ver detalhamento completo em [`INFRA.md`](INFRA.md). Resumo:

1. Rede: subnets privadas, Security Group, VPC Endpoints.
2. Secrets Manager: os 2 secrets (`ntrx/aad-credentials`, `ntrx/mtls-certificate`) —
   criados com placeholder e populados por processo de segurança separado (fora do
   pipeline de código).
3. DynamoDB: tabela `ntrx-connector-state` + item `CONFIG` inicial.
4. SQS: fila principal + DLQ.
5. S3: bucket RAW (SSE-KMS, Block Public Access, versionamento).
6. IAM: as **roles de execução** das duas Lambdas + a role de invocação do Scheduler
   (todas as roles podem ser criadas sem a função existir — são apenas trust policy +
   permissões, ver `INFRA.md §6`).
7. Publica todos os identificadores no SSM Parameter Store (`INFRA.md §7`).

A infra roda **antes** do primeiro deploy de aplicação (é a dependência de ordem entre as
pipelines — não entre os recursos individuais).

## O que a pipeline de aplicação cria

A aplicação lê os parâmetros do SSM (nenhum nome de recurso hardcoded) e executa, para
cada uma das duas Lambdas, um deploy que é **create-or-update** (idempotente — funciona
igual no primeiro deploy e nos seguintes, sem etapa "vazia" separada):

1. **Build + teste** do C# (.NET 8).
2. **Package**: `dotnet lambda package` → gera o zip.
3. **Ler parâmetros do SSM**: role ARN, subnets, security group, nome da tabela, URL/ARN
   da fila, nome do bucket, IDs dos secrets, base URL da API.
4. **Deploy da função** (`dotnet lambda deploy-function`, usando os
   `aws-lambda-tools-defaults.json` já presentes no repo + os valores lidos do SSM como
   parâmetros de linha de comando): cria a função se ainda não existe, ou atualiza
   código + configuração (memória, timeout, variáveis de ambiente, VPC) se já existe.
   - `ntrx-searcher`: memória 512 MB, timeout 900 s, reserved concurrency 1.
   - `ntrx-downloader`: memória 512 MB, timeout 300 s, reserved concurrency 2.
5. **Triggers** (create-or-update — a pipeline verifica se já existem antes de criar):
   - `ntrx-searcher`: dois schedules no EventBridge Scheduler (`ntrx-backfill`,
     `ntrx-incremental`, timezone `America/Sao_Paulo`, target = ARN da função recém
     publicada, usando a role de invocação já criada pela infra) — ver crons em
     `INFRA.md` (antiga §7, hoje descrita aqui).
   - `ntrx-downloader`: event source mapping da fila SQS (batch size 5, maximum
     concurrency 2, `ReportBatchItemFailures` habilitado) — só é possível depois que a
     função existe (motivo técnico #1 acima).
6. **Smoke test** pós-deploy: invocar `ntrx-searcher` com
   `{"mode":"incremental"}` e payload de teste / checar logs.

### Schedules (referência rápida, timezone `America/Sao_Paulo`)
| Schedule | Cron | Payload | Alvo |
|---|---|---|---|
| `ntrx-backfill` | `cron(0/20 0-7,18-23 ? * * *)` (+ fim de semana integral) | `{"mode":"backfill"}` | `ntrx-searcher` |
| `ntrx-incremental` | `cron(0/5 8-17 ? * MON-FRI *)` | `{"mode":"incremental"}` | `ntrx-searcher` |

Ajuste de horário = editar o schedule (não passa pela pipeline de novo). Variáveis de
ambiente das duas Lambdas seguem a tabela que estava em `INFRA.md` (agora alimentada
pelos parâmetros do SSM em vez de valores fixos):

| Variável | Origem (SSM) |
|---|---|
| `NTRX_BASE_URL` | `/ntrx-connector/api/base-url` |
| `STATE_TABLE` | `/ntrx-connector/state/table-name` |
| `AAD_SECRET_ID` | `/ntrx-connector/secrets/aad-secret-id` |
| `CERT_SECRET_ID` | `/ntrx-connector/secrets/cert-secret-id` |
| `QUEUE_URL` (só Searcher) | `/ntrx-connector/queue/url` |
| `RAW_BUCKET` (só Downloader) | `/ntrx-connector/s3/raw-bucket-name` |

## Ordem de execução resumida
```
1. Pipeline de infra roda (uma vez, e depois só quando a rede/dados/secrets mudam)
   → publica parâmetros no SSM
2. Pipeline de aplicação roda (a cada deploy de código)
   → lê SSM → cria/atualiza as 2 Lambdas → cria/atualiza triggers → smoke test
```
Depois do primeiro deploy, deploys seguintes da aplicação não tocam em nada que a infra
criou — só código, configuração e triggers da própria função.
