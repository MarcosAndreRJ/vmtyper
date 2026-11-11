# ADR 0007: Perfis iniciais de temporização e limites de segurança

- **Status:** Aceito (hipóteses sujeitas a calibração na Fase 3)
- **Data:** 2026-11-09
- **Fase:** F2 (Motor de temporização configurável)

## Contexto

Na Fase 0 (§1.3), a medição do motor v1 mostrou que `Task.Delay(1)`, `Task.Delay(3)` e `Task.Delay(10)` produziam a mesma mediana (~16 ms) quando a resolução global de timer do Windows oscilava para 15,625 ms (N5). Além disso, cada par de caracteres consecutivos recebia o mesmo intervalo fixo, sem distinção entre caracteres repetidos (`SameKeyMinGapMs`), pontuação ou quebras de linha em código.

Na Fase 2, introduzimos `HighResolutionDelayProvider` (`CreateWaitableTimerExW` com `CREATE_WAITABLE_TIMER_HIGH_RESOLUTION`) e `TypingTimingPolicy` parametrizado por `TypingProfile`. Os valores numéricos de cada perfil inicial são **hipóteses explícitas** e não presunções de segurança.

## Opções consideradas

1. **Tornar o perfil `Natural` padrão já na Fase 2**
   - *Rejeitado:* sem os experimentos sistemáticos de duplicação da Fase 3, não há evidência de que holds ou gaps longos reduzam duplicações no console VMware HTML5; holds longos acima do limiar de auto-repeat poderiam até induzir repetição.
2. **Manter `Legacy` como padrão na Fase 2 e expor `Fast`, `Compatibility`, `Natural` e `Custom` atrás da feature flag `AdaptiveTiming`**
   - *Aceito:* preserva o comportamento padrão conhecido (agora com temporização precisa via `HighResolutionDelayProvider`), enquanto permite testar e calibrar os demais perfis na Fase 3.

## Decisão

Definir quatro perfis predefinidos mais o modo `Custom`, todos limitados por `SafetyLimits(MinHoldMs: 1, MaxHoldMs: 60, MinGapMs: 0, MaxGapMs: 2000)`:

| Perfil | Hold (ms) | Gap (ms) | Pausas contextuais | Variação / Correlação (`φ`) | `SameKeyMinGapMs` | Hipótese associada |
|---|---|---|---|---|---|---|
| `Legacy` (padrão) | 2 fixo | `CharDelayMs` | Enter = `EnterDelayMs` | 0 / 0 | 0 | Reproduz o motor v1 com temporização de alta resolução |
| `Fast` | 2–4 | 3–8 | Enter = 20 ms | 8% / 0,35 | 6 ms | Piso mínimo entre teclas repetidas sem sacrificar vazão |
| `Compatibility` | 8–15 | 25–40 | Pontuação = 40 ms, Enter = 80 ms | 8% / 0,35 | 30 ms | Consoles HTML5 sob latência de rede ou carga de CPU |
| `Natural` | 30–60 | 60–180 | Palavra 80–200 ms (25%), Pont. 150–300 ms, Enter 300–600 ms | 22% / 0,75 | 75 ms | Cadência com deriva AR(1) e assimetria log-normal truncada |

O teto `MaxHoldMs = 60 ms` permanece abaixo de 250 ms (menor *repeat delay* configurável no Windows) para evitar disparo de auto-repeat pelo SO convidado. Na Fase 3, holds de 2, 15, 60 e 120 ms serão testados experimentalmente.

## Consequências

- O plano de eventos (`KeyStrokePlanner`) é 100% independente do `TypingProfile` (`Plan_IsIdentical_AcrossProfiles`).
- Dada uma semente (`Seed`), toda a sequência de `KeyTiming` é determinística e reprodutível (`SameSeed_SameTimingSequence`).

