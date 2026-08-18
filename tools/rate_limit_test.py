"""
Teste de limite de rate do endpoint de download de transcrição da API NTR-X.

O que faz:
  1. Autentica via AAD (client credentials) + mTLS (mesmo fluxo do conector C#).
  2. Busca as interações do último dia via POST /Search/Interactions.
  3. Dispara downloads de transcrição (GET .../Transcript) em paralelo, respeitando
     um rate-limit alvo configurável (requests/segundo) — o "rate otimista" que
     queremos validar antes de configurar a concorrência real das Lambdas.
  4. Registra status code, latência e tentativas de cada download e imprime um
     resumo no final (taxa de 429, latência, throughput real observado).

Instalação (uma vez):
    pip install requests cryptography

Variáveis de ambiente necessárias:
    NTRX_BASE_URL         ex.: https://host-ntrx/public/api/v1
    NTRX_ZONE_ID          GUID da zona (mesmo zoneId usado pelas Lambdas)
    AAD_TOKEN_ENDPOINT    ex.: https://login.microsoftonline.com/<tenant>/oauth2/v2.0/token
    AAD_CLIENT_ID
    AAD_CLIENT_SECRET
    AAD_SCOPE             ex.: <api-scope>/.default
    CERT_PFX_PATH         caminho local do .pfx do mTLS
    CERT_PFX_PASSWORD     senha do .pfx (vazio se não tiver)

Opcionais:
    CERT_EXTRA_HEADER_NAME / CERT_EXTRA_HEADER_VALUE  header extra do certificado, se a API exigir
    RATE_RPS              taxa alvo de downloads/segundo (default: 3.0)
    CONCURRENCY           workers paralelos (default: 3)
    MAX_DOWNLOADS         teto de transcrições baixadas no teste (default: 300)

Uso:
    python rate_limit_test.py

Roda a partir da pasta em que for chamado, e salva o CSV de resultados ali mesmo
(ntrx_rate_test_results_<timestamp>.csv), junto com o resumo impresso no console.

ATENÇÃO: isso bate na API real do NTR-X. Comece com RATE_RPS baixo (1-2) e vá
subindo aos poucos — o objetivo é achar onde a API começa a devolver 429, não
derrubar o serviço.
"""

from __future__ import annotations

import csv
import os
import statistics
import sys
import tempfile
import threading
import time
from concurrent.futures import ThreadPoolExecutor, as_completed
from dataclasses import dataclass, field
from datetime import datetime, timedelta, timezone

import requests
from cryptography.hazmat.primitives.serialization import Encoding, PrivateFormat, NoEncryption, pkcs12

MAX_ATTEMPTS = 4  # mesmo valor do NtrxApiClient.cs


def env(name: str, required: bool = True, default: str | None = None) -> str | None:
    value = os.environ.get(name, default)
    if required and not value:
        sys.exit(f"Variável de ambiente obrigatória não definida: {name}")
    return value


@dataclass
class Config:
    base_url: str
    zone_id: str
    token_endpoint: str
    client_id: str
    client_secret: str
    scope: str
    cert_pfx_path: str
    cert_pfx_password: str
    extra_header_name: str | None
    extra_header_value: str | None
    rate_rps: float
    concurrency: int
    max_downloads: int

    @staticmethod
    def load() -> "Config":
        return Config(
            base_url=env("NTRX_BASE_URL").rstrip("/") + "/",
            zone_id=env("NTRX_ZONE_ID"),
            token_endpoint=env("AAD_TOKEN_ENDPOINT"),
            client_id=env("AAD_CLIENT_ID"),
            client_secret=env("AAD_CLIENT_SECRET"),
            scope=env("AAD_SCOPE"),
            cert_pfx_path=env("CERT_PFX_PATH"),
            cert_pfx_password=env("CERT_PFX_PASSWORD", required=False, default="") or "",
            extra_header_name=env("CERT_EXTRA_HEADER_NAME", required=False),
            extra_header_value=env("CERT_EXTRA_HEADER_VALUE", required=False),
            rate_rps=float(env("RATE_RPS", required=False, default="3.0")),
            concurrency=int(env("CONCURRENCY", required=False, default="3")),
            max_downloads=int(env("MAX_DOWNLOADS", required=False, default="300")),
        )


class TokenBucket:
    """Limitador de taxa simples e thread-safe: solta no máximo `rate` tokens/segundo."""

    def __init__(self, rate: float, capacity: float | None = None):
        self.rate = rate
        self.capacity = capacity or max(rate, 1.0)
        self.tokens = self.capacity
        self.last = time.monotonic()
        self.lock = threading.Lock()

    def acquire(self) -> None:
        while True:
            with self.lock:
                now = time.monotonic()
                self.tokens = min(self.capacity, self.tokens + (now - self.last) * self.rate)
                self.last = now
                if self.tokens >= 1:
                    self.tokens -= 1
                    return
            time.sleep(0.01)


class AadTokenProvider:
    """Espelha AadTokenProvider.cs: client credentials, cacheado, renovado 5 min antes de expirar."""

    def __init__(self, cfg: Config):
        self.cfg = cfg
        self._token: str | None = None
        self._expires_at = datetime.min.replace(tzinfo=timezone.utc)
        self._lock = threading.Lock()

    def get_token(self) -> str:
        with self._lock:
            if self._token and datetime.now(timezone.utc) < self._expires_at:
                return self._token

            response = requests.post(
                self.cfg.token_endpoint,
                data={
                    "grant_type": "client_credentials",
                    "client_id": self.cfg.client_id,
                    "client_secret": self.cfg.client_secret,
                    "scope": self.cfg.scope,
                },
                timeout=30,
            )
            response.raise_for_status()
            payload = response.json()
            self._token = payload["access_token"]
            expires_in = int(payload.get("expires_in", 3600))
            self._expires_at = datetime.now(timezone.utc) + timedelta(seconds=max(expires_in - 300, 60))
            return self._token


def load_pfx_as_pem(pfx_path: str, password: str) -> tuple[str, str]:
    """Converte o .pfx em arquivos .pem temporários (requests não aceita pfx direto)."""
    with open(pfx_path, "rb") as f:
        pfx_bytes = f.read()

    key, cert, extra_certs = pkcs12.load_key_and_certificates(
        pfx_bytes, password.encode() if password else None
    )
    if key is None or cert is None:
        sys.exit("Não foi possível extrair chave/certificado do .pfx informado.")

    cert_fd, cert_path = tempfile.mkstemp(suffix=".pem")
    key_fd, key_path = tempfile.mkstemp(suffix=".pem")

    with os.fdopen(cert_fd, "wb") as f:
        f.write(cert.public_bytes(Encoding.PEM))
        for extra in extra_certs or []:
            f.write(extra.public_bytes(Encoding.PEM))

    with os.fdopen(key_fd, "wb") as f:
        f.write(key.private_bytes(Encoding.PEM, PrivateFormat.TraditionalOpenSSL, NoEncryption()))

    return cert_path, key_path


class NtrxApiClient:
    def __init__(self, cfg: Config, tokens: AadTokenProvider, cert_path: str, key_path: str):
        self.cfg = cfg
        self.tokens = tokens
        self.session = requests.Session()
        self.session.cert = (cert_path, key_path)
        if cfg.extra_header_name:
            self.session.headers[cfg.extra_header_name] = cfg.extra_header_value or ""

    def _send_with_retry(self, method: str, path: str, allow_not_found: bool = False, **kwargs) -> requests.Response:
        url = self.cfg.base_url + path
        for attempt in range(1, MAX_ATTEMPTS + 1):
            headers = kwargs.pop("headers", {}) or {}
            headers["Authorization"] = f"Bearer {self.tokens.get_token()}"
            response = self.session.request(method, url, headers=headers, timeout=120, **kwargs)

            if response.ok or (allow_not_found and response.status_code == 404):
                return response

            retryable = response.status_code == 429 or response.status_code >= 500
            if not retryable or attempt == MAX_ATTEMPTS:
                response.raise_for_status()

            retry_after = response.headers.get("Retry-After")
            delay = float(retry_after) if retry_after else 5 * attempt
            time.sleep(delay)

        raise RuntimeError("unreachable")

    def search_last_day(self) -> list[dict]:
        now = datetime.now(timezone.utc)
        start = now - timedelta(days=1)
        body = {
            "QueryCondition": {
                "Combinator": "And",
                "QueryFilters": [
                    {
                        "Column": "StartTime",
                        "Operation": "Between",
                        "Value": [
                            start.strftime("%Y-%m-%dT%H:%M:%S+00:00"),
                            now.strftime("%Y-%m-%dT%H:%M:%S+00:00"),
                        ],
                    }
                ],
            }
        }
        response = self._send_with_retry(
            "POST", "Search/Interactions", json=body, headers={"Content-Type": "application/json"}
        )
        return parse_interactions(response.json())

    def get_transcript(self, interaction_id: str) -> requests.Response:
        return self._send_with_retry(
            "GET",
            f"Search/Interactions/{interaction_id}/Transcript?zoneId={self.cfg.zone_id}",
            allow_not_found=True,
            headers={"Accept": "application/json"},
        )


ID_KEYS = ("interactionId", "InteractionId", "id", "Id")


def parse_interactions(payload) -> list[dict]:
    """Parser tolerante ao schema, espelhando SearchResultParser.cs."""

    def find_array(node):
        if isinstance(node, list):
            return node
        if not isinstance(node, dict):
            return None
        for value in node.values():
            if isinstance(value, list) and value and isinstance(value[0], dict) and _read_id(value[0]):
                return value
        for key, value in node.items():
            if key.lower() == "interactions" and isinstance(value, list):
                return value
        for value in node.values():
            if isinstance(value, dict):
                found = find_array(value)
                if found is not None:
                    return found
        return None

    array = find_array(payload)
    if not array:
        return []
    return [item for item in array if isinstance(item, dict) and _read_id(item)]


def _read_id(item: dict) -> str | None:
    for key in ID_KEYS:
        if key in item and isinstance(item[key], str):
            return item[key]
    return None


@dataclass
class DownloadResult:
    interaction_id: str
    status: str  # "downloaded" | "no_transcript" | "error"
    status_code: int
    latency_ms: float
    error: str = ""


def run_download_test(client: NtrxApiClient, interaction_ids: list[str], cfg: Config) -> list[DownloadResult]:
    bucket = TokenBucket(rate=cfg.rate_rps, capacity=max(cfg.rate_rps, cfg.concurrency))
    results: list[DownloadResult] = []
    results_lock = threading.Lock()

    def worker(interaction_id: str) -> DownloadResult:
        bucket.acquire()
        start = time.monotonic()
        try:
            response = client.get_transcript(interaction_id)
            latency_ms = (time.monotonic() - start) * 1000
            status = "no_transcript" if response.status_code == 404 else "downloaded"
            return DownloadResult(interaction_id, status, response.status_code, latency_ms)
        except requests.HTTPError as ex:
            latency_ms = (time.monotonic() - start) * 1000
            code = ex.response.status_code if ex.response is not None else -1
            return DownloadResult(interaction_id, "error", code, latency_ms, str(ex))
        except Exception as ex:  # timeout, conexão etc.
            latency_ms = (time.monotonic() - start) * 1000
            return DownloadResult(interaction_id, "error", -1, latency_ms, str(ex))

    print(f"Disparando {len(interaction_ids)} downloads — alvo {cfg.rate_rps} req/s, "
          f"{cfg.concurrency} workers paralelos...")

    start_all = time.monotonic()
    with ThreadPoolExecutor(max_workers=cfg.concurrency) as pool:
        futures = {pool.submit(worker, iid): iid for iid in interaction_ids}
        done = 0
        for future in as_completed(futures):
            result = future.result()
            with results_lock:
                results.append(result)
            done += 1
            if done % 20 == 0 or done == len(interaction_ids):
                elapsed = time.monotonic() - start_all
                print(f"  {done}/{len(interaction_ids)} concluídos — {done/elapsed:.2f} req/s reais até agora")

    return results


def print_summary(results: list[DownloadResult], elapsed_seconds: float) -> None:
    total = len(results)
    downloaded = sum(1 for r in results if r.status == "downloaded")
    no_transcript = sum(1 for r in results if r.status == "no_transcript")
    errors = [r for r in results if r.status == "error"]
    rate_limited = sum(1 for r in errors if r.status_code == 429)

    latencies = sorted(r.latency_ms for r in results)
    p50 = statistics.median(latencies) if latencies else 0
    p95 = latencies[int(len(latencies) * 0.95) - 1] if latencies else 0

    print("\n=== Resumo do teste ===")
    print(f"Total de requisições:      {total}")
    print(f"Baixadas com sucesso:      {downloaded}")
    print(f"Sem transcrição (404):     {no_transcript}")
    print(f"Erros:                     {len(errors)}  (dos quais 429/rate-limit: {rate_limited})")
    print(f"Tempo total:               {elapsed_seconds:.1f}s")
    print(f"Throughput real observado: {total / elapsed_seconds:.2f} req/s")
    print(f"Latência p50 / p95:        {p50:.0f}ms / {p95:.0f}ms")
    if rate_limited > 0:
        print(f"\n>> A API retornou 429 {rate_limited}x — esse é o sinal de que {rate_limited} requisições "
              f"excederam o limite real no rate/concorrência testados.")
    else:
        print("\n>> Nenhum 429 recebido neste rate — pode tentar subir RATE_RPS/CONCURRENCY e repetir.")


def write_csv(results: list[DownloadResult], path: str) -> None:
    with open(path, "w", newline="", encoding="utf-8") as f:
        writer = csv.writer(f)
        writer.writerow(["interaction_id", "status", "status_code", "latency_ms", "error"])
        for r in results:
            writer.writerow([r.interaction_id, r.status, r.status_code, f"{r.latency_ms:.1f}", r.error])
    print(f"\nResultados detalhados salvos em: {path}")


def main() -> None:
    cfg = Config.load()
    tokens = AadTokenProvider(cfg)
    cert_path, key_path = load_pfx_as_pem(cfg.cert_pfx_path, cfg.cert_pfx_password)

    try:
        client = NtrxApiClient(cfg, tokens, cert_path, key_path)

        print("Buscando interações do último dia (POST /Search/Interactions)...")
        interactions = client.search_last_day()
        print(f"{len(interactions)} interações encontradas.")

        if len(interactions) > 100:
            print(">> Aviso: resposta com mais de 100 itens — se a API paginar, este script não segue "
                  "página seguinte (schema de paginação não confirmado com a NICE). Resultado pode estar truncado.")

        interaction_ids = [_read_id(i) for i in interactions][: cfg.max_downloads]
        if not interaction_ids:
            print("Nenhuma interação encontrada no último dia — nada para testar.")
            return

        print(f"Testando download de {len(interaction_ids)} transcrições "
              f"(MAX_DOWNLOADS={cfg.max_downloads}).")

        start = time.monotonic()
        results = run_download_test(client, interaction_ids, cfg)
        elapsed = time.monotonic() - start

        print_summary(results, elapsed)

        timestamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        write_csv(results, f"ntrx_rate_test_results_{timestamp}.csv")
    finally:
        os.remove(cert_path)
        os.remove(key_path)


if __name__ == "__main__":
    main()
