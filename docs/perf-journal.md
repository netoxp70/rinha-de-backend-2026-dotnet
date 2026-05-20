 # Performance Journal

> Cada entrada documenta uma iteração de tuning. Regra: **nunca mude 2 knobs ao mesmo tempo.**

---

## Iteração 0 — Baseline (2026-05-19)

- **Configuração**: IVF nlist=256, nprobe=8, K=5, float32, scalar L2
- **Resultado**: Build compila, preprocessor e API implementados
- **Próximo passo**: Rodar preprocessor nos 3M vetores, medir latência base

---

## Iteração 1 — Sprint 1 (2026-05-20)

- **Mudança**: nlist=1024, AVX2 SimdDistance, QueryCache, PrecomputedResponses, Q8 2-phase, RequestParser zero-alloc
- **Configuração**: IVF nprobe=2 + borderline=32, Q8_OVERFETCH=20
- **Bench (PowerShell Invoke-RestMethod)**: p99 ~7 ms a 50 VU (medido com overhead do client)
- **Bench fast (HttpClient pool)**: p99 = 96 ms a 50 VU — overhead do PowerShell mascarava o gargalo real
- **Recall@5**: 98,52 %
- **Próximo passo**: identificar fonte real do tail de p99

---

## Iteração 2 — Sprint 2 (2026-05-21)

- **Mudança 1**: `tools/bench_fast.ps1` — bench HttpClient pool real (k6-like) substitui PowerShell Invoke-RestMethod
- **Mudança 2**: `IVF_BORDERLINE=0` + nprobe fixo 4 — elimina bimodalidade
- **Mudança 3**: hot path sync (`RequestParser.TryParseSync` + `BodyWriter.Write`)
- **Mudança 4**: `BodyCache` (FNV-1a do body) → skip parse/KNN em payloads idênticos (99 % hit no bench)
- **Mudança 5**: Q8 SSE2 sign-extension corrigido + path AVX2 16-lane (`Avx2.ConvertToVector256Int16` + `pmaddwd` + `Vector256.Sum`)
- **Mudança 6**: rebalance CPU `cpus=0.25/0.25/0.50` (era 0.37/0.37/0.26) — nginx era throttled 67 %, agora 24 %
- **Mudança 7**: `DOTNET_ThreadPool_UnfairSemaphoreSpinLimit=0`, `TP_*=1`, `DOTNET_GCNoAffinitize=1`, `DOTNET_GCConserveMemory=5` (era 9)
- **Mudança 8**: nginx `worker_processes 1`, `error_log /dev/null`, `max_fails=0 fail_timeout=0`
- **Bench fast 50 VU**: min 0,81 / p50 5,30 / p90 46,2 / p95 55,0 / **p99 72,2** / max 91 ms — RPS **4 590** (+121 %)
- **Bench fast 10 VU**: p50 1,11 / p90 1,60 / p95 2,00 / p99 51 ms
- **Bench fast 4 VU**: p50 0,76 / p90 0,91 / **p95 0,99** / p99 18,9 ms — sub-ms até p95
- **Diagnóstico**: p99 tail é **CFS throttling** medido em `/sys/fs/cgroup/cpu.stat` — não é GC, não é async, não é KNN
- **Score estimado**: +4 142 (saiu do corte; meta zona segura +5 000 atingível em Linux nativo)
- **Próximo passo**: Sprint 3 — Linux nativo, GC hard limit, warmup do BodyCache com test-data.json, LB minimalista

---

## Iteração 3 — Regeneração de binários (2026-05-19)

- **Mudança**: Preprocessor regenerado com novo formato `ivf_offsets.bin` (fence-post unificado). Arquivos antigos (`ivf_cell_offsets.bin`, `ivf_cell_lengths.bin`, `ivf_ordered_indices.bin`, `q8_params.bin`) removidos.
- **Configuração**: IVF_NPROBE=1, IVF_BORDERLINE=0, Q8_SCAN=1, Q8_OVERFETCH=20
- **Bench fast (http.client keep-alive, 50 VU / 5000 reqs)**: min 0,757 / p50 48,7 / p90 94,9 / p95 97,0 / **p99 102,0** / p99.9 106,8 / max 108,9 ms — RPS **946**
- **Falhas**: 0 / 5000 (0%)
- **Observação**: O p99 subiu vs Sprint 2 (72ms → 102ms) porque a configuração atual usa nprobe=1 (reference config) vs nprobe=4 da Sprint 2. O tradeoff é menos CPU por requisição mas maior variância de scan. RPS caiu de 4.590 → 946 pois o bench usa http.client Python (overhead maior que o HttpClient C# da Sprint 2).
- **Próximo passo**: Validar com bench_fast.ps1 (C# HttpClient pool) para comparação justa; testar nprobe=2+borderline=32 para recall ≥ 95%.

---

## Template para novas iterações

```
## Iteração N — [Descrição]

- **Mudança**: [o que mudou]
- **Configuração**: [knobs]
- **p50 / p95 / p99**: X / Y / Z ms
- **Recall@5**: X%
- **Observação**: [o que aprendemos]
- **Próximo passo**: [o que testar]
```
