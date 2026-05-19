# Tuning Knobs

Variáveis de ambiente configuráveis para ajuste de performance.

## IVF & KNN

| Variável | Default | Range | Efeito |
|----------|---------|-------|--------|
| `IVF_NPROBE` | 1 | 1–256 | Número de células IVF a escanear. ↓ = mais rápido, ↓ recall |
| `IVF_BORDERLINE_NPROBE` | 32 | 1–256 | Nprobe usado quando score é borderline (0.4 ou 0.6) |
| `IVF_BORDERLINE` | (enabled) | "0" para desativar | Desativa borderline re-probe |
| `DATA_DIR` | `data` | path | Diretório com binários preprocessados |
| `WARMUP` | (enabled) | "0" para desativar | Desativa warmup no startup |
| `WARMUP_ITERS` | 64 | 1–N | Número de queries aleatórias no warmup |

## .NET Runtime

| Variável | Default | Range | Efeito |
|----------|---------|-------|--------|
| `TP_MIN_WORKERS` | 2 | 1–8 | ThreadPool min worker threads |
| `TP_MAX_WORKERS` | 4 | 1–8 | ThreadPool max worker threads |
| `DOTNET_GCHeapCount` | (auto) | 1–N | Número de heaps GC |
| `DOTNET_PROCESSOR_COUNT` | (auto) | 1–N | Override de contagem de CPU |
| `DOTNET_gcServer` | 0 | 0/1 | Workstation (0) vs Server (1) GC |
| `DOTNET_GCConserveMemory` | 9 | 0–9 | Agressividade de conservação de memória |
| `DOTNET_GCDynamicAdaptationMode` | 0 | 0/1 | DATAS GC (heap dinâmico) |
| `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit` | (default) | 0–N | 0 = sem spin, reduz CPU idle |

## Rede

| Variável | Default | Range | Efeito |
|----------|---------|-------|--------|
| `UDS_PATH` | (vazio) | path | Se setado, escuta em Unix Domain Socket |
| `PORT` | 9999 | 1–65535 | Porta TCP (usado se UDS_PATH vazio) |

## Docker Compose (recursos)

| Serviço | CPU | RAM | Nota |
|---------|-----|-----|------|
| api1 | 0.37 | 150MB | Ajustável se lb usar menos |
| api2 | 0.37 | 150MB | Idem |
| lb | 0.26 | 50MB | Pode reduzir se API precisar mais |
| **Total** | **1.00** | **350MB** | Máximo da Rinha |
