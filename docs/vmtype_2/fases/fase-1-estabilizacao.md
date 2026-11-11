# Fase 1: Estabilização e confiabilidade

**Objetivo:** corrigir todos os defeitos confirmados (A–I e N1–N4, N8–N10, N13), com testes de regressão, sem mudar o ritmo de digitação (isso fica para a F2).

**Pré-requisitos:** F0 concluída (abstrações, projeto de testes, receptor local).

**Regra:** cada seção abaixo vira um ou mais PRs independentes, na ordem §5 → §1 → §4 → §2 → §3 → §6. O coordenador de sessão vem primeiro porque §1 e §3 dependem do "sessão ativa" centralizado.

---

## §5 Coordenador de sessão (N3, N10, D parcial)

### 5.1 `TypingSessionCoordinator` (novo, `Services/`)
Responsabilidade: **no máximo uma sessão de injeção por processo**, venha ela da janela principal ou da de diagnóstico.

```csharp
public sealed class TypingSessionCoordinator
{
    public bool IsActive { get; }
    public event Action<bool>? ActiveChanged;
    // Retorna null se já houver sessão ativa.
    public TypingSessionLease? TryBegin(string origin);   // "Main" | "Diagnostics"
}
public sealed class TypingSessionLease : IDisposable { CancellationToken Token; PauseToken Pause; void Cancel(); ... }
```
- É dono do registro do **ESC global**: registra no `TryBegin` e desregistra no `Dispose`. Nenhuma outra classe registra ESC.
- `MainViewModel.StartAsync` e `DiagnosticsWindow.SendTest_Click` passam a usar `TryBegin`. A trava `Interlocked` atual vai para dentro do coordenador.
- `DiagnosticsWindow.SendTest_Click`: envolver tudo em `try/catch (Exception)` com log e mensagem (hoje é `async void` e não captura exceções genéricas).

### 5.2 Pausa durante a contagem (N10)
`TogglePause` passa a aceitar `AguardandoContagem`: a contagem congela e retoma do número em que parou.

### Testes
`Coordinator_SecondBegin_ReturnsNull`, `Coordinator_DisposeReleasesEsc`, `Diagnostics_WhileMainActive_IsRefused`, `PauseDuringCountdown_FreezesCountdown`.

---

## §1 Fila de digitação (A, B, D, N9)

### 1.1 Novo modelo de estado
`Models/TextBlockStatus.cs`:

| Estado | Significado | Persistido como |
|---|---|---|
| `Empty` | sem texto (ou só espaços) | `Empty` |
| `Ready` | tem texto e pode ser digitado | `Ready` |
| `Typing` | em envio agora | → `Interrupted` ao carregar |
| `Paused` | sessão pausada neste bloco | → `Interrupted` ao carregar |
| `Sent` | todas as unidades foram **enviadas** (não "confirmadas") | `Sent` |
| `PartiallySent` | cancelado ou falhou depois de enviar ≥ 1 unidade | `PartiallySent` + `SentUpToIndex` |
| `Cancelled` | cancelado antes da primeira unidade | volta a `Ready` |
| `Failed` | erro antes de enviar qualquer unidade | `Failed` + `LastError` |
| `Interrupted` | app encerrou durante `Typing`/`Paused`: entrega desconhecida | `Interrupted` |

- **"Próximo" deixa de ser status.** Passa a ser `TypingQueueService.NextBlockId`, calculado assim:
  1. Se `ManualNextId` aponta para um bloco existente em `Ready`/`PartiallySent`/`Interrupted`/`Failed`, ele é o próximo.
  2. Caso contrário, é o primeiro bloco em `Ready`, na ordem.
- `TextBlockItem` ganha `IsNext` (calculado e notificado pelo serviço), `SentUpToIndex`, `LastError` e `LastSessionId`.
- **Migração do JSON v1:** `Pending` + texto → `Ready`; `Pending` vazio → `Empty`; `Current` → `Ready`; `Completed` → `Sent`; `Typing` → `Interrupted`.

### 1.2 Tabela de transições (implementar como método único `Transition(id, evento)`)
| De \ Evento | TextEdited | BeginTyping | Pause/Resume | AllSent | Cancel | Fail | SetAsNext | Restart |
|---|---|---|---|---|---|---|---|---|
| Empty | Ready (se texto) | ✗ | ✗ | ✗ | ✗ | ✗ | ✗ | Empty |
| Ready | Empty (se vazio) | Typing | ✗ | ✗ | ✗ | ✗ | Manual=id | Ready |
| Typing | **bloqueado** | ✗ | Paused | Sent | PartiallySent¹/Ready² | PartiallySent¹/Failed² | ✗ | ✗ |
| Paused | **bloqueado** | ✗ | Typing | ✗ | PartiallySent¹/Ready² | idem | ✗ | ✗ |
| Sent | Ready³ | ✗ | ✗ | ✗ | ✗ | ✗ | Ready + Manual=id | Ready |
| PartiallySent / Interrupted / Failed | Ready (zera índice) | Typing⁴ | ✗ | ✗ | ✗ | ✗ | Manual=id | Ready |

¹ se `SentUpToIndex > 0`. ² se nenhuma unidade foi enviada. ³ editar um bloco enviado o devolve a `Ready`, com aviso na UI. ⁴ exige confirmação: "Reenviar do início" ou "Continuar da unidade N (texto restante: …)". O padrão é **do início**, nunca automático.

### 1.3 Correções específicas
- **A:** `OnBlockPropertyChanged(Text)` → `Transition(id, TextEdited)` + recálculo de `NextBlockId` + `Changed`.
- **B:** `SetAsNext` grava `ManualNextId` e não altera o status de outros blocos. Teste: blocos [Ready, Ready, Sent] e SetAsNext(3) → próximo = 3.
- **D:** `TypingQueueService.IsLocked` (ligado pelo coordenador). Todos os mutadores (`AddBlock`, `RemoveBlock`, `Move*`, `SetAsNext`, `RestartQueue`, `NewQueue`) retornam `false` e logam quando a fila está travada. Na UI, os botões da barra da fila, **Configurações** e **Teste de Teclado** ficam ligados a `CanEditQueue` (`!IsBusy`). O `TextBox` do bloco em `Typing` vira `IsReadOnly`.
- **N9:** `TypingSession` ganha `BlockId` (Guid) e `SessionId`. `BlockOrder` continua só para exibição.
- **Sem avanço indevido:** só `AllSent` muda o ponteiro do próximo para o bloco seguinte.

### Testes (unitários, com `IQueueStore` em memória)
Cobertura de cada célula da tabela 1.2, mais os testes da F0 para A, B e D (retirar o skip), migração v1→v2 com JSON real de exemplo, e `Interrupted` ao carregar um JSON com `Typing`.

---

## §4 Persistência confiável e logs (F, N4)

### 4.1 `AtomicJsonStore<T>` (novo, `Services/Persistence/`)
```
Save:  serializa → grava em <arq>.tmp → FileStream.Flush(true) →
       File.Replace(<arq>.tmp, <arq>, <arq>.bak)   (ou File.Move se <arq> não existir)
Load:  tenta <arq> → valida → OK
       falhou? tenta <arq>.bak → OK + aviso "restaurado do backup"
       falhou? renomeia <arq> para <arq>.corrupt-<yyyyMMddHHmmss> → retorna LoadResult.Corrupt
```
- `LoadResult<T> { T? Value; LoadStatus Status (Ok|RestoredFromBackup|Missing|Corrupt); string? Detail; }`. **Nunca retorna "vazio" quando o arquivo é corrompido.**
- Envelope versionado: `{ "schemaVersion": 2, "savedAt": "...", "data": ... }`. O v1 (array puro ou objeto sem envelope) é migrado na leitura.
- Validação depois de desserializar: IDs únicos, `Order` contíguo, `SentUpToIndex <= Text.Length`, enums válidos. Se falhar, segue o mesmo caminho de `Corrupt`.
- Falha ao **salvar**: o evento `PersistenceFailed(string)` é exibido na barra de status (com a mensagem "As alterações não estão sendo salvas") e o próximo save tenta de novo.
- Aplicar em `queue.json`, `settings.json` e `history.json`.
- Quando o carregamento for `Corrupt`, a UI mostra um diálogo: "A fila salva não pôde ser lida. Uma cópia foi preservada em … Começar com fila vazia?". **O arquivo corrompido nunca é sobrescrito.**

### 4.2 Privacidade do texto
- `AppSettings.QueueTextPersistence = Full | Encrypted | None`.
  - `Encrypted`: `ProtectedData.Protect(..., DataProtectionScope.CurrentUser)` (pacote `System.Security.Cryptography.ProtectedData`) aplicado ao campo `Text`.
  - `None`: grava blocos sem texto. Ao reabrir, os blocos aparecem como `Empty`, com a nota "texto não persistido".
- O padrão continua `Full` (comportamento atual), com a opção explicada na tela.

### 4.3 Logs sem conteúdo (N4)
- Logs comuns registram índice, **categoria** (letra/dígito/espaço/pontuação/acentuado/controle), método, VK e scan. Nunca o caractere.
- Substituições registram só "N substituições aplicadas (regras #1, #3)".
- `DetailedTrace` (feature flag, desligada) libera o caractere, com aviso na UI. Ela é desligada automaticamente ao fechar o app, e o arquivo vai para `diagnostics/` com um nome que indica o conteúdo sensível.

### Testes
`CorruptQueueFile_IsPreservedAndReported`, `TruncatedTmp_DoesNotAffectMain`, `BackupIsUsedWhenMainCorrupt`, `V1Array_IsMigrated`, `EncryptedRoundTrip`, `NonePersistence_DoesNotWriteText`, `Log_NeverContainsTypedChar` (varre o log depois de digitar um texto-sentinela).

---

## §2 Motor de teclado (E, G, H, N1, N2, N8)

### 2.1 Planejamento separado da execução
`KeyStrokePlanner` (novo, puro):
```csharp
sealed record InputUnit(int Index, string Text /*grapheme*/, UnitKind Kind, IReadOnlyList<KeyEvent> Events, string? UnsupportedReason);
readonly record struct KeyEvent(KeyEventKind Kind /*Down|Up*/, KeyEncoding Enc /*Scan|Unicode*/, ushort Code, bool Extended);
PreflightReport Plan(string text, PlanOptions o);  // Units + contagens + lista de não suportados
```
- Itera por **grapheme** (`StringInfo.GetTextElementEnumerator`). Pares substitutos viram uma unidade. No modo Unicode, envia as duas metades: down alto, down baixo, up baixo, up alto.
- Substituições aplicadas **sobre unidades** e numa única passada, sem encadeamento (N8). Regras com o mesmo `De` disparam aviso na tela de configurações. Combinações viram `UnitKind.Combo` diretamente, sem caractere sentinela.
- **Tecla morta literal (N1):** depois de `VkKeyScanEx`, detectar se o VK é uma tecla morta no layout (`MapVirtualKeyEx(vk, MAPVK_VK_TO_CHAR)` com o bit alto ligado). Se for, e o caractere desejado for o próprio acento, gerar *tecla morta + Espaço*. Testes com tabela ABNT2 e US-Intl: `~a`, `~ `, `^^`, `'a'` (US-Intl), `"x"`.
- **Extended (G):** `ResolveScan` usa `MAPVK_VK_TO_VSC_EX`. Prefixo `0xE0` → `Extended = true`, mais uma tabela de fallback que inclui `VK_DIVIDE`, `LWIN`, `RWIN`, `APPS`, `SNAPSHOT`, `RCONTROL`, `RMENU`, setas e Ins/Del/Home/End/PgUp/PgDn.
- **Layout de destino (N2):** `AppSettings.TargetLayout = HostForeground | <KLID>` (por exemplo, `00010416` ABNT2, `00020409` US-Intl), usando `LoadKeyboardLayout(KLID, KLF_NOTELLSHELL)` só para mapear. A UI mostra o layout em uso e avisa que "deve coincidir com o layout configurado na VM".
- **Pré-voo (E):** se houver unidades `Unsupported`, a sessão **não começa em silêncio**. Um diálogo lista as posições e categorias com as opções "Cancelar" (padrão), "Enviar mesmo assim, pulando N unidades" ou "Usar Unicode para essas". Nunca pula sem decisão.

### 2.2 Execução com garantia de liberação (H)
`KeyStrokeExecutor` + `InjectedKeyTracker`:
1. **Antes da sessão:** aguardar até 2 s que nenhum modificador **físico** esteja pressionado (`GetAsyncKeyState` de Shift/Ctrl/Alt/Win, descontando o que está no tracker). Se o tempo esgotar, a sessão falha com a mensagem "Solte Ctrl/Shift/Alt antes de iniciar" (H4).
2. **Cada KeyDown é enviado numa chamada `SendInput` de 1 evento.** Retorno 1 → `tracker.Add(code, extended, encoding)`. Retorno 0 → registrar `GetLastError` (5 → "destino elevado/UIPI") e abortar a unidade.
3. **Folga KeyDown→KeyUp:** continua não cancelável (o valor da F2 vem de `IDelayProvider`).
4. **KeyUps em ordem inversa,** cada um em uma chamada. Sucesso → `tracker.Remove`.
5. `finally` da unidade: `tracker.ReleaseAll()` solta **somente** o que o tracker contém, com a **mesma codificação** usada na descida (H2). Novas tentativas: até 3 vezes, com 5 ms entre elas, e falha logada como `StuckKeyRisk`.
6. **Removido:** `ReleaseAllModifiers` incondicional no fim do bloco (H3). Ele continua existindo só como último recurso em `DispatcherUnhandledException`, e mesmo assim baseado no tracker.
7. O resultado de cada unidade é `UnitResult { Index, Outcome (Sent|Skipped|Failed), EventsSent, NativeError, TimestampDown, TimestampUp }`, consumido por progresso, métricas e diagnóstico.

> **Decisão a registrar em ADR:** enviar 1 evento por chamada aumenta o número de chamadas, mas torna o "sucesso parcial" trivial de tratar (0 ou 1). Com isso, `SendInput` em lote deixa de ser usado para teclas.

### 2.3 Progresso honesto (E)
`TypingProgress` passa a ter `TotalUnits`, `Sent`, `Skipped`, `Failed`. O total vem do **plano** (unidades), não de `Text.Length`. A conclusão com `Skipped > 0` ou `Failed > 0` termina como `PartiallySent`/aviso, nunca como "Concluído" verde.

### 2.4 Encerramento durante a digitação (H5)
`MainWindow.Closing`: se houver sessão ativa, cancelar, aguardar a task terminar (com timeout de 2 s) e só então fechar. `OnExit` faz o mesmo como rede de segurança.

### Testes (com `RecordingInjector` e layout falso)
- Para cada unidade, a sequência de eventos é balanceada (todo Down tem Up com a mesma codificação).
- Falha parcial simulada em cada posição de cada combo → nenhum Down fica órfão.
- Cancelamento em todas as posições possíveis → nenhum Down órfão e índice correto.
- Bloco de sucesso → zero KeyUp sem Down correspondente.
- Modificador físico simulado → a sessão não começa.
- Testes de N1, G, E e N8 da F0 com o skip retirado.
- Integração: corpus de acentuação, símbolos e tecla morta literal no receptor local → texto idêntico.

---

## §3 TargetGuard (C, N13)

### 3.1 Âncora
No fim da contagem, capturar `TargetAnchor { HWND (raiz via GetAncestor(GA_ROOT)), PID, ProcessName, Title, CapturedAt }`. Isso falha se:
- o foreground for o próprio VmTyper;
- `ValidateTargetWindow` estiver ligado e o processo não for o navegador esperado.

### 3.2 Monitoramento
- **Evento:** `SetWinEventHook(EVENT_SYSTEM_FOREGROUND, ..., WINEVENT_OUTOFCONTEXT)` na UI thread. Ao disparar, marca `TargetLost` (volatile).
- **Verificação síncrona:** antes de cada unidade, o executor compara `GetForegroundWindow()` (raiz) com a âncora. A chamada é barata e cobre a corrida com o evento. O PID e o nome ficam em cache na âncora (N13).
- **Título:** troca de aba muda o título. No modo `TargetGuardStrict`, mudança de título → pausa. No padrão, só registra um aviso, porque consoles mudam o título sozinhos.

### 3.3 Reação
- Destino perdido → **pausa automática** (estado `Pausado: destino perdido`), um incidente é registrado `{Index, de→para, timestamp}` e a unidade em curso termina normalmente (ela nunca é interrompida no meio).
- Para retomar: o usuário clica em Continuar ou usa o atalho → contagem curta (padrão 2 s) → revalida a âncora (mesmo HWND) → segue. Se o HWND mudou, o diálogo "O destino mudou. Usar a nova janela como destino?" exige confirmação explícita.
- **Padrão:** o guard fica **ligado** no modo HWND (não exige navegador específico). Essa é uma mudança de padrão justificada pela diretriz de fidelidade e registrada em ADR. Ele pode ser desligado nas configurações.

### 3.4 Limitações (documentar na UI e no README)
O guard detecta troca de **janela de topo**. Ele não detecta troca de aba sem mudança de título, foco em outro frame ou campo da página, nem se a VM está com o foco interno no campo certo.

### Testes
Unitários com `IForegroundWindowProvider` falso: perder o foco no meio → pausa no índice correto, sem unidade cortada. Retomar com outro HWND → exige confirmação. Integração: abrir um segundo receptor no meio da digitação → o segundo receptor recebe 0 caracteres.

---

## §6 Configurações (I)
1. A substituição do tipo Combinação sem tecla capturada bloqueia o salvamento, com a mensagem por linha.
2. Limites: `CharDelayMs`/`EnterDelayMs` de 0 a 2000, `CountdownSeconds` de 0 a 30. Valores fora → mensagem de erro.
3. Configurações bloqueadas durante a sessão (§1.3 D).
4. `settings.json` corrompido → aviso e cópia preservada (§4.1) antes de abrir a tela com padrões.
5. Botão "Restaurar padrões" (com confirmação).
6. Teste de ida e volta: `AppSettings` → JSON → `AppSettings` igual. Teste de que cada controle da tela mapeia para o campo certo (extraído para um `SettingsMapper` testável).

---

## Critérios de aceite da F1
- [ ] Todos os testes marcados `Known` na F0 passam, sem nenhum skip restante para A–I e N1–N4/N8–N10/N13.
- [ ] O corpus básico, de acentuação e de tecla morta chega idêntico ao receptor local (Físico e Unicode).
- [ ] O teste de "segunda janela no meio da digitação" passa.
- [ ] Um JSON corrompido nunca é sobrescrito (teste + verificação manual).
- [ ] Nenhum KeyUp órfão em sucesso, cancelamento ou falha simulada.
- [ ] Relatório F1 com a lista de arquivos alterados, os testes e os resultados.

## Fora do escopo da F1
Ritmo/perfis (F2), métricas de duplicação (F3), UI nova além do necessário para bloquear e avisar (F5).
