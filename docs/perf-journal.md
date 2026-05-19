# Performance Journal

> Cada entrada documenta uma iteração de tuning. Regra: **nunca mude 2 knobs ao mesmo tempo.**

---

## Iteração 0 — Baseline (2026-05-19)

- **Configuração**: IVF nlist=256, nprobe=8, K=5, float32, scalar L2
- **Resultado**: Build compila, preprocessor e API implementados
- **Próximo passo**: Rodar preprocessor nos 3M vetores, medir latência base

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
