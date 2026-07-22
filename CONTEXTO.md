# Contexto do Projeto — Integração NTR-X (Gravações/Transcrições)

> Arquivo de contexto consolidado. Fontes: briefing do Wesley (17/07/2026) + prints em `contexto/`.

## 1. Objetivo de negócio

Democratização de dados de ligações no cliente: exportar gravações/transcrições da API
NTR-X (NICE) e disponibilizar os dados para as áreas de negócio (ex.: Asset, Corretora)
de forma **controlada** — cada área acessa **somente os próprios dados**.

A aplicação roda **na rede interna do cliente** (a API NTR-X é interna).

## 2. API NTR-X Public API (v1, OAS 3.0)

- Base route: `/public/api/v1`
- Grupos de métodos (licenciados separadamente): Downloads, LCM, Playback, Provisioning, Search.

### Autenticação (print `autenticação swagger.jpeg`)
- OpenID Connect: token JWT do provedor externo no header `Authorization: Bearer <token>`.
- Token validado pela Public API via campos Authority e Audience.
- Para auditoria, o token deve conter User Id, Name, Username → solicitar token com scopes `openid` e `profile` (providers: Ping, AAD, ADFS).
- Autorização: usuário autenticado precisa do claim "Public API: Access" no NTR-X + claims específicos por método.

**Como já está sendo feito no cliente:**
- App registrada no AD (Azure AD/Entra) do cliente.
- Credenciais da app (client credentials) geram o token.
- Token Bearer no header das requisições + secret do certificado instalado no ambiente (**mTLS ativado**).

### Limites de rate (print `api limits.jpeg`)
| Grupo | Limite |
|---|---|
| Download job calls | 1 chamada/segundo |
| LCM calls | 100 chamadas/segundo |
| Playback | 3 chamadas concorrentes |
| Provisioning | 10 chamadas/segundo |
| ~~Search calls~~ | ~~1 chamada/minuto~~ — **não se confirmou nos testes (17/07/2026)**: chamadas ocorrem sem esse limite. Mesmo assim, manter pacing conservador. |

- Throttling global: cada instância da Public API suporta até **100 requests concorrentes no total**; excedeu → HTTP **429** (client deve segurar as chamadas).
- **Latência observada do Search**: a requisição leva **pelo menos ~15 segundos** para completar → dimensionar timeouts do HttpClient/Lambda e o planejamento de janelas por isso.

### Endpoints relevantes
**Busca de interações** (print `endpoint busca interactionsid.jpeg`):
- `POST /Search/Interactions` — busca por query. Body com `QueryCondition`/`QueryFilters`, ex.:
  ```json
  {"QueryCondition":{"Combinator":"And","QueryFilters":[
    {"Column":"StartTime","Operation":"Between",
     "Value":["2023-01-01T00:00:00+00","2023-01-31T23:59:59+00"]}]}}
  ```
- Filtros vistos: `StartTime` (Between), `CaptureClusterId` (Equals), `interactionType` (In: Voice/Chat), `duration` (GreaterThan, ms).

**Download de transcrição** (print `download transcript.jpeg`):
- `GET /Search/Interactions/{interactionId}/Transcript`
- Params obrigatórios: `interactionId` (path, GUID) e `zoneId` (query, GUID) — **zoneId será variável de configuração da aplicação**.
- Formatos via header `Accept`: `application/json` (default), `text/vtt`, `text/eml`.

## 3. Requisitos funcionais

1. **Backfill histórico**: exportar ligações de **01/01/2023 até hoje** (~3,5 anos).
   - Deve rodar "mais forte" **fora do horário de mercado** (mercado = 08h–18h) → backfill roda à noite/madrugada/fins de semana.
2. **Incremental (near real-time)**: terminado o backfill, coletar as ligações novas **a cada 5 minutos**, rodando **apenas no horário de mercado** (08h–18h).
3. **Agendamento dinâmico**: controlado por cron ou similar, com janelas/horários **ajustáveis de forma prática** (sem redeploy).
4. Respeitar os rate limits da API (especialmente Search = 1/min).
5. Segregação de acesso por área no consumo dos dados (Lake Formation / partições por área).

## 4. Desenho do PM (prints `desenho ntrx 1.jpeg` e `desenho ntrx 2.jpeg`)

**Parte 1 — Ingestão:**
- NICE NTR-X API (Gravações Teams + Transcrições) →
- EventBridge Scheduler (agenda coleta) → **Lambda ou ECS Fargate** (Conector NTR-X API)
- Apoio: Secrets Manager (credencial/API token), SQS DLQ (falhas/reprocessamento), CloudWatch Logs/Metrics (observabilidade), CloudTrail (auditoria de acesso, KMS, permissões)
- Saída: **S3 RAW Bucket** (restrito à squad, SSE-KMS)

**Parte 2 — Curadoria e consumo:**
- Glue/Lambda/ECS (curadoria e enriquecimento) → decisão **"Identificou área com confiança?"**
  - **Não** → S3 Quarantine (sem democratizar)
  - **Sim** → S3 Curated (particionado por área) → Glue Data Catalog → **Lake Formation (permissões por área)** → Athena / API interna / BI
    - Asset acessa só Asset; Corretora acessa só Corretora.

## 5. Stack e decisões

- Linguagem: **C# (.NET)**
- Deploy: **AWS**
- Compute: avaliar **Lambda vs ECS Fargate** (preferência do time: Lambda, se der conta e não ficar caro)
- Mensageria: avaliar necessidade; se usar, será **SQS**
- Config dinâmica de agendamento: requisito forte

## 6. Riscos / pontos em aberto

- Backfill enumera ~1.290 dias de histórico → janelamento de busca (ex.: 1 dia por chamada,
  ~15s cada) + confirmar paginação/tamanho de página do Search com a NICE.
- Ambas as Lambdas (Searcher e Downloader) chamam a mesma API → **as duas** precisam do
  certificado mTLS e das credenciais AAD (Secrets Manager compartilhado).
- Volume de ligações/dia desconhecido → dimensionar throughput e custo depois dessa informação.
- Token AAD via client credentials pode não conter claims de usuário (openid/profile) exigidos
  para auditoria — já funciona no cliente, mas documentar.
- mTLS: certificado de cliente precisa estar acessível ao compute (Secrets Manager/ACM) e a
  conectividade AWS ↔ rede interna do cliente (VPC + Direct Connect/VPN) é pré-requisito.
- Gravações (áudio) além das transcrições? Se sim, entra o grupo Downloads (1/s, download jobs).
