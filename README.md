# Rinha de Backend 2026 — .NET 11 Fraud Detection

API HTTP de detecção de fraude via KNN (k=5) em 3M vetores de 14 dimensões, compilada com NativeAOT em .NET 11.

## Arquitetura

```
client :9999
   ↓
nginx (round-robin, UDS, sem lógica)   cpuset=2,3
   ├── api1  cpuset=0  (core físico dedicado)
   └── api2  cpuset=1  (core físico dedicado)
```

| Serviço | CPU | RAM | cpuset | Descrição |
|---------|-----|-----|--------|-----------|
| api1 | 0.45 | 150MB | 0 | .NET 11 NativeAOT, IVF+Q8, mmap |
| api2 | 0.45 | 150MB | 1 | .NET 11 NativeAOT, IVF+Q8, mmap |
| lb   | 0.10 | 50MB  | 2,3 | nginx, least_conn, UDS |
| **Total** | **1.00** | **350MB** | | |

## Stack

- **.NET 11 preview** — NativeAOT, AOT compilado para `linux-x64`
- **IVF** (nlist=1024) + **Q8 quantização** com scan AVX2/SSE
- **MemoryMappedFile** — page cache compartilhado entre réplicas (mesmo inode)
- **UDS via tmpfs** — sem overhead de TCP loopback entre nginx e APIs
- **BodyCache** (65536 slots, FNV-1a 8-byte loop) — skip total de parse+KNN em cache hit
- **QueryCache** (4096 slots) — skip KNN em vetores repetidos
- **Respostas pré-computadas** — 6 scores possíveis serializados na startup
- **cpuset pinning** — elimina CFS throttling dando um core físico por API
- **seccomp=unconfined** — elimina overhead de syscall filtering

## Build & Run

```bash
# Subir (inclui build da imagem + preprocessamento do dataset)
docker compose up --build -d

# Verificar saúde
curl http://localhost:9999/ready

# Teste de um request
curl -s -X POST http://localhost:9999/fraud-score \
  -H "Content-Type: application/json" \
  -d '{"id":"tx-1","transaction":{"amount":4368.82,"installments":8,"requested_at":"2026-03-17T02:04:06Z"},"customer":{"avg_amount":68.88,"tx_count_24h":18,"known_merchants":["MERC-004","MERC-015"]},"merchant":{"id":"MERC-062","mcc":"7801","avg_amount":25.55},"terminal":{"is_online":true,"card_present":false,"km_from_home":881.61},"last_transaction":{"timestamp":"2026-03-17T01:58:06Z","km_from_current":660.92}}'
```

## Benchmark (medir p99 via linha de comando)

### Opção 1 — `wrk` (Linux/WSL, recomendado)

```bash
# Instalar: apt install wrk
# Criar arquivo de payload
cat > /tmp/payload.lua << 'EOF'
wrk.method = "POST"
wrk.headers["Content-Type"] = "application/json"
wrk.body = '{"id":"tx-L1","transaction":{"amount":41.12,"installments":2,"requested_at":"2026-03-11T18:45:53Z"},"customer":{"avg_amount":82.24,"tx_count_24h":3,"known_merchants":["MERC-003","MERC-016"]},"merchant":{"id":"MERC-016","mcc":"5411","avg_amount":60.25},"terminal":{"is_online":false,"card_present":true,"km_from_home":29.23},"last_transaction":null}'
EOF

# 50 VUs, 30s, reporta latência com percentis
wrk -t 4 -c 50 -d 30s -s /tmp/payload.lua --latency http://localhost:9999/fraud-score
```

### Opção 2 — `hey` (multiplataforma, binário único)

```bash
# Instalar: go install github.com/rakyll/hey@latest
# ou baixar: https://github.com/rakyll/hey/releases

hey -n 50000 -c 50 -m POST \
  -H "Content-Type: application/json" \
  -d '{"id":"tx-L1","transaction":{"amount":41.12,"installments":2,"requested_at":"2026-03-11T18:45:53Z"},"customer":{"avg_amount":82.24,"tx_count_24h":3,"known_merchants":["MERC-003","MERC-016"]},"merchant":{"id":"MERC-016","mcc":"5411","avg_amount":60.25},"terminal":{"is_online":false,"card_present":true,"km_from_home":29.23},"last_transaction":null}' \
  http://localhost:9999/fraud-score
```

### Opção 3 — `curl` (sem percentis, mas funciona em qualquer lugar)

```bash
# Mede tempo de um request único (cache hit após warmup)
curl -s -o /dev/null -w "total: %{time_total}s\n" \
  -X POST http://localhost:9999/fraud-score \
  -H "Content-Type: application/json" \
  -d '{"id":"tx-L1","transaction":{"amount":41.12,"installments":2,"requested_at":"2026-03-11T18:45:53Z"},"customer":{"avg_amount":82.24,"tx_count_24h":3,"known_merchants":["MERC-003","MERC-016"]},"merchant":{"id":"MERC-016","mcc":"5411","avg_amount":60.25},"terminal":{"is_online":false,"card_present":true,"km_from_home":29.23},"last_transaction":null}'
```

### Opção 4 — benchmark C# incluso no projeto (Windows/Linux)

```bash
# Requer .NET 10+ SDK instalado localmente (NÃO rodar da pasta do projeto — global.json exige .NET 11)
# Rodar de qualquer outra pasta:
dotnet run --project /caminho/para/tools/bench_cs/bench_cs.csproj -c Release \
  -- http://localhost:9999/fraud-score 50000 50 2000
# args: URL  total_requests  concurrency  warmup_requests
```

## Por que `cpuset` é crítico para p99

O Docker por padrão usa **CFS quota** (`cpus: "0.45"` = 45ms de CPU a cada 100ms). Quando 50 VUs chegam simultaneamente e esgotam essa quota, o container fica **congelado por até 100ms** — causando p99~100ms independente de quão rápido seja o código.

Com `cpuset: "0"`, a API tem o **core físico inteiro** sem quota. Não há throttling — o scheduler nunca congela o processo. Resultado: p99 determinístico baseado apenas na latência real do código (~0.6ms).

## Estrutura

```
src/
├── Api/                    # ASP.NET Core Minimal API (NativeAOT)
│   ├── Program.cs          # Kestrel UDS, GC/TP tuning, hot path sync
│   ├── Infrastructure/
│   │   ├── IvfIndex.cs     # IVF index (mmap, Q8, AVX2/SSE scan)
│   │   ├── KnnEngine.cs    # IVF → top-5 → fraud_score
│   │   ├── BodyCache.cs    # Cache direto por hash do body HTTP
│   │   ├── QueryCache.cs   # Cache direto por hash do vetor
│   │   ├── RequestParser.cs # Zero-alloc Utf8JsonReader
│   │   ├── SimdDistance.cs  # L2² AVX2/SSE/scalar
│   │   └── PrecomputedResponses.cs
│   └── Models/
├── Preprocessor/           # CLI: references.json.gz → binários IVF+Q8
│   └── Program.cs
├── Shared/
│   ├── Constants.cs
│   ├── VectorTypes.cs
│   ├── Vectorizer.cs
│   └── MccRisk.cs
docker/
├── Dockerfile.api          # Multi-stage: build(SDK11) → dataprep → runtime-deps
└── nginx.conf              # 2 workers, UDS upstream, keepalive 512
tools/
└── bench_cs/               # Benchmark C# com HttpClient keep-alive pool
```

## Documentação

- [docs/PROGRESS.md](docs/PROGRESS.md) — Tracker de progresso
- [docs/perf-journal.md](docs/perf-journal.md) — Diário de performance
- [docs/tuning-knobs.md](docs/tuning-knobs.md) — Variáveis de tuning
