# Fase 2: Motor de temporização configurável ("digitação humana e adaptativa")

**Objetivo:** substituir os intervalos fixos, e hoje imprecisos (N5), por um motor de temporização **preciso, configurável e reproduzível**, com perfis. O comportamento atual continua disponível como perfil **Legado**, que segue sendo o padrão até que a F3 produza evidência para mudar.

**Pré-requisitos:** F1 (executor com `IDelayProvider`, `UnitResult`) e F1B.

**Princípio inviolável:** a temporização afeta apenas **quando** cada unidade é enviada. O conteúdo do plano de teclas nunca é alterado: nada de erros simulados, Backspace ou autocorreção.

---

## §1 Temporização precisa (pré-condição de tudo)

### 1.1 `IDelayProvider`
```csharp
public interface IDelayProvider
{
    void HoldExact(TimeSpan d);                                   // não cancelável (KeyDown→KeyUp)
    ValueTask WaitAsync(TimeSpan d, CancellationToken ct);        // cancelável (entre unidades)
    long Timestamp();                                             // Stopwatch.GetTimestamp
}
```

### 1.2 `HighResolutionDelayProvider`
- **Entre unidades:** `CreateWaitableTimerExW(NULL, NULL, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION /*0x2*/, TIMER_ALL_ACCESS)` (Windows 10 1803+). Espera com `WaitForMultipleObjects` (timer + handle de cancelamento), numa thread dedicada de digitação, e não no pool.
- **Fallback** quando a flag não é suportada: espera híbrida (`Thread.Sleep(1)` até faltar cerca de 1,5 ms, depois spin com `Stopwatch`). Isso **não** depende de `timeBeginPeriod` global.
- **Hold (KeyDown→KeyUp):** spin com `Stopwatch` para durações ≤ 5 ms, e híbrido acima disso. Nunca é cancelável.
- **Thread de digitação dedicada** (`new Thread { IsBackground = true, Priority = AboveNormal }`), alimentada por um `Channel` ou pela própria sessão. Isso reduz a preempção de thread do pool (N12). Prioridade acima de `AboveNormal` não é usada.
- **Medição:** cada espera registra o valor solicitado e o obtido (alimenta §5).

### 1.3 Teste de precisão (integração, local)
Repetir o experimento da F0 §0.8 com o novo provedor, com o sistema ocioso e sob carga. **Registrar** mediana, p95, máximo e uso de CPU (via `Process.TotalProcessorTime`) para o legado e para o novo. Não há meta numérica prévia: o relatório apresenta o que foi medido.

---

## §2 Modelo de perfil

```csharp
public sealed record TypingProfile
{
    string Name; ProfileKind Kind;        // Legacy | Fast | Natural | Compatibility | Custom
    Range HoldMs;                         // ex.: [2,2] no Legacy
    Range GapMs;                          // intervalo KeyUp(n) → KeyDown(n+1)
    int WordPauseMs; Range WordPauseChance;
    int PunctuationPauseMs;               // após . , ; : ! ?
    int EnterPauseMs;
    double SpeedVariationPct;             // amplitude da deriva de "tempo"
    double Correlation;                   // φ do AR(1), 0..0.95
    int SameKeyMinGapMs;                  // piso entre unidades idênticas consecutivas (hipótese F3)
    SafetyLimits Limits;                  // pisos/tetos absolutos
    int? Seed;                            // null = aleatório; valor = reprodutível
}
public sealed record SafetyLimits(int MinHoldMs = 1, int MaxHoldMs = 60, int MinGapMs = 0, int MaxGapMs = 2000);
```

**Perfis iniciais.** Os valores são **hipóteses**, registradas em ADR e marcadas para calibração na F3:

| Perfil | Hold | Gap | Pausas | Variação | Racional da hipótese |
|---|---|---|---|---|---|
| **Legado** | 2 fixo | `CharDelayMs` | Enter = `EnterDelayMs` | 0 | Reproduz o v1 exatamente (agora com atraso preciso) |
| **Rápido** | 2–4 | 3–8 | Enter 20 | baixa | Desempenho com um piso mínimo |
| **Compatibilidade** | 8–15 | 25–40 | pont. 40, Enter 80 | baixa | Consoles com latência alta, à semelhança do preset "Compatibilidade" atual (30/60) |
| **Natural** | 30–90 | 60–180 | palavra 80–200 (ocasional), pont. 150–300, Enter 300–600 | média, correlacionada | Ritmo próximo do de uma pessoa. **Não é presumido mais seguro** |
| **Personalizado** | todos os campos editáveis | | | | |

> **Atenção (2.4 do prompt):** o `MaxHoldMs` padrão (60 ms) fica bem abaixo do *repeat delay* mínimo do Windows (250 ms). Um hold maior **não** é presumido mais seguro, e a F3 testa explicitamente holds de 2, 15, 60 e 120 ms.

---

## §3 `TypingTimingPolicy` (função pura e determinística dada uma semente)

```csharp
KeyTiming Next(TimingContext ctx);   // ctx: unidade atual, anterior, próxima, posição na palavra/linha, tipo de linha
readonly record struct KeyTiming(TimeSpan Hold, TimeSpan GapAfter, string Reason);
```

Algoritmo:
1. **Deriva de tempo correlacionada:** `tempo_t = φ·tempo_{t−1} + (1−φ)·1 + ε`, com `ε ~ N(0, σ)` truncado. σ deriva de `SpeedVariationPct`. Isso evita sorteios independentes por caractere.
2. **Gap base:** amostrado de uma distribuição **log-normal truncada** em `GapMs` (assimetria realista e limitada) e multiplicado por `tempo_t`.
3. **Pausas contextuais** (somadas):
   - fim de palavra: espaço com probabilidade `WordPauseChance`, e mais comprida em palavras longas;
   - pontuação;
   - Enter;
   - **linhas de código** (heurística: indentação ≥ 2 espaços ou caracteres `{}();=` acima de um limiar) com um pequeno fator extra configurável. Hipótese: editores com autoindentação/autocomplete precisam de mais tempo.
4. **Unidades idênticas consecutivas:** aplicar `SameKeyMinGapMs` (piso), para as hipóteses de "KeyUp perdido" e de debounce.
5. **Hold:** amostrado em `HoldMs`, com uma correlação fraca com o tempo.
6. **Clamp final** em `SafetyLimits`. O `Reason` registra quais regras atuaram (para diagnóstico).
7. RNG: `new Random(seed)`, encapsulado (e não `Random.Shared`), para garantir reprodutibilidade.

---

## §4 `TypingScheduler`
Une o plano (F1 §2.1), a política, o executor e o guard:
```
para cada unidade u:
    aguardar pausa / checar cancelamento / checar TargetGuard
    t = policy.Next(ctx)
    result = executor.Execute(u, t.Hold)          // não cancelável dentro da unidade
    metrics.Record(u, t, result)
    await delay.WaitAsync(t.GapAfter, ct)         // cancelável
```
- O **pré-voo** mostra a duração estimada do bloco (soma dos valores esperados) antes de iniciar.
- **Modo "dry-run"** (sem injetar) gera a sequência de `KeyTiming` para os testes e para a UI de prévia.
- O `KeyboardInputService` atual vira uma fachada fina sobre o scheduler, preservando `IKeyboardInputService`.

---

## §5 `TypingMetrics`
Por sessão: unidades tentadas, eventos enviados, unidades puladas, falhas nativas, CPS médio, tempo total, histograma de gap e hold (solicitado vs obtido), CPU do processo durante a sessão e incidentes de foco. Exposto como `SessionMetrics` (imutável no fim) e persistido no histórico (só números).

"Duplicações confirmadas" **não** entram aqui. Elas vêm da F3 e só existem quando há receptor.

---

## §6 Configuração e UI mínima nesta fase
- `AppSettings.TypingProfile` (nome) + `AppSettings.CustomProfile`.
- Os presets atuais (Instantâneo, Muito rápido etc.) continuam funcionando como variantes do Legado.
- A UI completa fica para a F5. Aqui basta um ComboBox de perfil na aba Digitação e a feature flag `AdaptiveTiming` (desligada = só Legado visível).

---

## Testes
| Teste | Garante |
|---|---|
| `SameSeed_SameTimingSequence` | reprodutibilidade |
| `AllTimings_WithinSafetyLimits` (property-based, 10k unidades por perfil) | limites |
| `Plan_IsIdentical_AcrossProfiles` | **o conteúdo nunca muda com o perfil** |
| `Simulated_Receiver_Reconstructs_OriginalText` (injetor falso + modelo de teclado) | integridade |
| `Natural_IsNotIid` (autocorrelação lag-1 > 0 quando φ > 0) | correlação |
| `PunctuationAndEnter_GetLongerGaps` | pausas contextuais |
| `Cancel_DuringGap_IsImmediate` / `Cancel_DuringHold_WaitsForKeyUp` | semântica de cancelamento |
| `Legacy_Matches_V1_EventSequence` | compatibilidade |
| Integração: corpus completo no receptor local com cada perfil → texto idêntico | ponta a ponta local |

## Critérios de aceite da F2
- [ ] Todos os perfis produzem sequências válidas e o texto é preservado (teste e receptor local).
- [ ] Nenhuma regressão de cancelamento ou liberação (suíte da F1 verde).
- [ ] Precisão de atraso medida e publicada no relatório F2 (legado vs novo, ocioso vs carga).
- [ ] O Legado continua sendo o padrão.
