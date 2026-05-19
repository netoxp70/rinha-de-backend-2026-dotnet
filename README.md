# Rinha de Backend 2026 — .NET 11 Fraud Detection

API HTTP de detecção de fraude via KNN (k=5) em 3M vetores de 14 dimensões.

## Arquitetura

```
client → nginx (LB, UDS) → api1 (Kestrel UDS, IVF+KNN)
                          → api2 (Kestrel UDS, IVF+KNN)
```

| Serviço | CPU | RAM | Descrição |
|---------|-----|-----|-----------|
| api1 | 0.37 | 150MB | .NET 11 NativeAOT, IVF index, mmap |
| api2 | 0.37 | 150MB | .NET 11 NativeAOT, IVF index, mmap |
| lb | 0.26 | 50MB | nginx, least_conn, UDS |
| **Total** | **1.00** | **350MB** | |

## Stack

- **.NET 11** (NativeAOT, PublishAot=true)
- **IVF** (Inverted File Index) com k-means++ (nlist=256)
- **MemoryMappedFile** para page cache compartilhado entre réplicas
- **UDS** (Unix Domain Sockets) via tmpfs entre nginx e APIs
- **JSON source-gen** (zero reflection)
- **Pre-computed responses** (6 possíveis fraud_scores pré-serializados)
- **GC tuning**: Workstation + SustainedLowLatency

## Build & Run

```bash
# Build e subir com Docker Compose
docker compose up --build

# Testar
curl http://localhost:9999/ready
curl -X POST http://localhost:9999/fraud-score \
  -H "Content-Type: application/json" \
  -d '{"id":"tx-1","transaction":{"amount":384.88,"installments":3,"requested_at":"2026-03-11T20:23:35Z"},"customer":{"avg_amount":769.76,"tx_count_24h":3,"known_merchants":["MERC-009","MERC-001"]},"merchant":{"id":"MERC-001","mcc":"5912","avg_amount":298.95},"terminal":{"is_online":false,"card_present":true,"km_from_home":13.7},"last_transaction":{"timestamp":"2026-03-11T14:58:35Z","km_from_current":18.8}}'
```

## Desenvolvimento local (sem Docker)

```bash
# 1. Preprocessar dataset (gera binários em data/)
dotnet run --project src/Preprocessor -- resources/references.json.gz data/

# 2. Rodar API (porta 9999 por padrão)
dotnet run --project src/Api
```

## Estrutura

```
src/
├── Api/                    # ASP.NET Core Minimal API
│   ├── Program.cs          # Kestrel UDS, endpoints, GC tuning
│   ├── Infrastructure/
│   │   ├── IvfIndex.cs     # IVF index (mmap, cell scan, L2)
│   │   ├── KnnEngine.cs    # Orquestra IVF → top-5 → fraud_score
│   │   └── PrecomputedResponses.cs
│   └── Models/
├── Preprocessor/           # CLI: JSON.gz → k-means++ → binários
│   └── Program.cs
├── Shared/                 # Tipos e lógica compartilhados
│   ├── Constants.cs
│   ├── VectorTypes.cs
│   ├── Vectorizer.cs
│   └── MccRisk.cs
docker/
├── Dockerfile.api          # Multi-stage (build → preprocess → runtime)
└── nginx.conf              # UDS upstream, least_conn, reuseport
```

## Atribuição

Padrões adotados: IVF, mmap compartilhado, UDS, pre-baked responses, GC tuning.

## Documentação

- [docs/PROGRESS.md](docs/PROGRESS.md) — Tracker de progresso com critérios de aceite
- [docs/perf-journal.md](docs/perf-journal.md) — Diário de performance
- [docs/tuning-knobs.md](docs/tuning-knobs.md) — Variáveis de tuning
