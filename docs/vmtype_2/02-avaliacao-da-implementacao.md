# Avaliação da implementação atual

Esta é uma auditoria estática do código no commit `fd90a31` (branch `master`), feita lendo todos os arquivos `.cs` e `.xaml` do projeto. Os testes que confirmam cada item são escritos na [Fase 0](fases/fase-0-auditoria-e-linha-de-base.md). Até lá, o status máximo de qualquer item é *Confirmado no código*.

## 1. Linha de base observada

| Item | Valor |
|---|---|
| Branch / commit | `master` / `fd90a31 primeiro commit` (anterior: `2e90841`) |
| Remoto | `https://github.com/MarcosAndreRJ/vmtyper.git` |
| Alterações locais | nenhuma no projeto; pasta `rea/` não rastreada na raiz (sem relação com o VmTyper, **não tocar**) |
| SDKs instalados | apenas `10.0.401` |
| Runtimes Desktop | `8.0.31` e `10.0.12` |
| Build `Debug` | **0 erros, 0 avisos de código** (com SDK 10 apontando para `net8.0-windows`) |
| Testes automatizados | **nenhum** (não existe projeto de testes) |
| Tamanho | ~3.700 linhas de C#/XAML próprias, em 33 arquivos |

**Evidência importante sobre o repositório:** `bin/` e `obj/` estão versionados e não há `.gitignore`. Um único `dotnet build` modificou **29 arquivos rastreados** (eles foram restaurados com `git checkout -- VmTyper/obj` e `git clean -f -- VmTyper/obj`). Por isso qualquer build suja a árvore de trabalho, o que atrapalha a regra do prompt de "identificar e preservar alterações locais". Esse é o motivo de a higiene do repositório ter sido trazida para a Fase 0.

**Prazo de suporte:** o .NET 8 sai de suporte em **10/11/2026**, ou seja, amanhã em relação à data deste documento. Ver [fase-1b](fases/fase-1b-migracao-dotnet10.md).

## 2. Mapa da arquitetura atual

```
App.xaml.cs (composition root manual + service locator estático App.XxxInstance)
 ├─ MainViewModel ──► IKeyboardInputService ──► KeyboardInputService ──► Win32 (static)
 │                 │                                   └─► KeyboardMappingService (VkKeyScanEx / NFD)
 │                 ├─► TypingQueueService ──► QueuePersistenceService (queue.json)
 │                 ├─► ActiveWindowService (nome do processo em foreground)
 │                 ├─► GlobalHotkeyService (RegisterHotKey em janela HWND_MESSAGE)
 │                 ├─► SettingsService (settings.json) / HistoryService (history.json)
 │                 └─► LogService (memória + %AppData%\VmTyper\logs)
 ├─ SettingsWindow (code-behind manual; lê/grava settings.json diretamente)
 └─ DiagnosticsWindow (chama IKeyboardInputService diretamente)
```

**Pontos positivos que devem ser preservados:**
- `Win32` é o único ponto de P/Invoke, e a interface `IKeyboardInputService` já existe como fronteira.
- KeyDown e KeyUp vão em chamadas `SendInput` separadas, e a folga entre eles não pode ser cancelada.
- Existe uma trava atômica de reentrância (`Interlocked.CompareExchange`) em `MainViewModel.StartAsync`.
- `Shift+Home` sem `Delete` às cegas, que é uma decisão de segurança correta.
- `ConfigureAwait(false)` no laço de digitação: a injeção não depende da UI thread.
- Pausa e cancelamento só são observados *entre* unidades de envio.

**O que limita a testabilidade:**
- `Win32` é `internal static` e `KeyboardInputService` chama métodos estáticos. Não há como injetar um falso.
- `TypingQueueService` depende de `Dispatcher` (através de `DispatcherTimer`) e de `QueuePersistenceService` concreto.
- O `App` funciona como service locator estático, usado por `MainWindow` e `DiagnosticsWindow`.

## 3. Problemas conhecidos do prompt (A–I)

### A: Fila não reconhece texto novo · **Confirmado no código** · Alta
- `VmTyper/Services/TypingQueueService.cs:70-77`: `OnBlockPropertyChanged` só reinicia o timer de autosave quando `Text` muda. `RecalculateStatuses()` não é chamado.
- **Efeito 1:** um bloco vazio que recebe texto continua `Pending` e o botão mostra "DIGITAR" desabilitado até que outra ação (mover, adicionar) recalcule.
- **Efeito 2 (inverso):** um bloco `Current` que é esvaziado continua `Current`. Ao acionar, `StartCoreAsync` (`MainViewModel.cs:350-356`) cai em `Erro` ("ficou vazio") em vez de pular para o próximo bloco elegível.
- **Correção:** recalcular quando `Text` muda (se não houver sessão ativa) e notificar `Changed`.

### B: `SetAsNext()` não seleciona o bloco pedido · **Confirmado no código** · Alta
- `TypingQueueService.cs:194-207` só rebaixa blocos com `Order >= alvo`. `RecalculateStatuses` (`:294-320`) dá `Current` ao **primeiro** bloco não concluído e não vazio. Se existir um bloco `Pending` com texto antes do alvo, é ele quem vira `Current`.
- **Correção:** separar o "ponteiro do próximo" do status. Manter `NextBlockId` explícito (override manual), que `Recalculate` respeita enquanto o alvo continuar elegível.

### C: Destino não é monitorado durante a transmissão · **Confirmado no código** · Alta
- `MainViewModel.cs:335-345`: uma única verificação, logo depois da contagem, e só se `ValidateTargetWindow` estiver ligado. O padrão em `AppSettings.cs` é `false`.
- A verificação compara apenas o nome do processo (`ActiveWindowService.cs`). Ela não confere HWND, título nem se o foco voltou para o próprio VmTyper.
- Se o usuário trocar de janela (Alt+Tab, notificação, pop-up), o texto continua sendo injetado em **qualquer** janela que esteja em primeiro plano.

### D: Fila mutável durante a digitação · **Confirmado no código** · Alta
- `MainWindow.xaml:38-42`: os botões **ADICIONAR**, **REINICIAR FILA** e **NOVA FILA** ficam *fora* do `Border IsEnabled=!IsBusy` (linha 47). Os botões **Configurações** e **Teste de Teclado** (linhas 22-23) também continuam ativos.
- **NOVA FILA durante a digitação:** `NewQueue()` faz `Blocks.Clear()` e remove o bloco `Typing`. Ao final, `CompleteBlock`/`CancelBlock` não o encontram (`Find` retorna null) e saem em silêncio. O histórico registra "Concluído" para um bloco que não existe mais.
- **REINICIAR FILA durante a digitação:** rebaixa todos os outros blocos, inclusive os já concluídos.
- A camada de serviço protege só parte das operações (`RemoveBlock`/`Move*`/`SetAsNext` ignoram o bloco `Typing`). `AddBlock`, `NewQueue` e `RestartQueue` não têm proteção.

### E: Caracteres ignorados contam como enviados · **Confirmado no código** · Média
- `KeyboardInputService.cs:112-113`: `sent++` acontece incondicionalmente depois de `SendCharacter`, mesmo quando o retorno é `"Ignorado"` (`:240-241`). Uma conclusão a 100% pode esconder omissões. O único aviso fica no log.
- O total também diverge: `MainViewModel.cs:308` usa `next.Text.Length` (texto cru, com `\r\n` e sem substituições), e o serviço depois reporta `normalized.Length`.
- Pares substitutos (emoji, alguns símbolos) são iterados como dois `char`. No modo físico, cada metade falha e é "ignorada" em separado.

### F: Persistência perde dados em silêncio · **Confirmado no código** · **Crítica**
- `QueuePersistenceService.cs` (`Load`): qualquer exceção (JSON corrompido, arquivo bloqueado, gravação truncada) retorna uma lista **vazia**. Em seguida, `LoadInternal` cria um bloco vazio. O próximo `Save()`, disparado pelo autosave ou por `AddBlock`, **sobrescreve o arquivo original**. A fila preparada é perdida sem nenhum aviso.
- `Save` usa `File.WriteAllText` direto: não é atômico, um travamento no meio deixa o arquivo truncado e a exceção é engolida.
- `SettingsService` repete o padrão. A `SettingsWindow` então carrega os padrões e, ao salvar, sobrescreve o `settings.json` corrompido. As substituições cadastradas são perdidas.
- O texto integral dos blocos fica em `queue.json`, em texto puro, sem opção de desativar.

### G: Teclas estendidas · **Confirmado no código** · Média
- `Win32.cs:103-112` (`IsExtendedKey`) não inclui `VK_DIVIDE (0x6F)`, `VK_LWIN/RWIN (0x5B/0x5C)`, `VK_APPS (0x5D)` nem `VK_SNAPSHOT (0x2C)`.
- **Impacto concreto:** a `SettingsWindow` permite capturar "/ (numpad)" numa substituição do tipo Combinação. O envio usa `MapVirtualKeyEx(VK_DIVIDE)` → scan `0x35` **sem** a flag estendida, que corresponde à tecla `/`(US) ou `;`(ABNT2) do teclado principal, não ao `/` do numpad. É justamente o tipo de caractere que a substituição deveria contornar.
- O Enter do numpad não pode ser distinguido pelo VK. Teclas do numpad também dependem do estado de NumLock **da VM**, que é desconhecido.
- **Correção preferida:** usar `MAPVK_VK_TO_VSC_EX` (já declarado em `Win32.cs:125`, mas sem uso), que devolve o prefixo `0xE0`/`0xE1`, e manter uma tabela explícita só como fallback.

### H: Liberação de teclas · **Confirmado no código** · Alta
1. **Tecla principal presa:** em `SendKeyCombo` (`KeyboardInputService.cs:340-342`), se `SendInputs(ups)` lançar exceção, ou se `SendInputs(downs)` for parcial (por exemplo, Shift desceu e a letra não), a exceção sobe. O `finally` chama `ReleaseAllModifiers`, que solta **apenas modificadores** e não a tecla principal.
2. **Liberação inconsistente:** as descidas vão por *scan code* (`KEYEVENTF_SCANCODE`), mas `ReleaseAllModifiers` solta por *virtual key* (`Win32.cs:305-325`). Um receptor que rastreia por scan code, como o console VMware, pode não casar o par.
3. **Seis KeyUp espúrios por bloco:** o `finally` roda também em caso de **sucesso**. A cada bloco, a VM recebe 6 KeyUp sem KeyDown correspondente (LShift, RShift, LCtrl, RCtrl, LAlt, RAlt).
4. **Modificadores físicos do usuário:**
   - **Contaminação:** o atalho padrão é `Ctrl+Shift+Enter`. Com `CountdownSeconds = 0`, ou se o usuário ainda estiver segurando as teclas, o Ctrl/Shift físico se soma aos eventos injetados (`SendInput` não reseta o estado físico). Os primeiros caracteres chegam como atalhos (`Ctrl+Shift+letra`).
   - **Interferência:** o `ReleaseAllModifiers` final pode "soltar" logicamente uma tecla que o usuário ainda segura.
5. **Saída do aplicativo durante a digitação:** `Shutdown()` (`MainViewModel.cs:475-482`) cancela, mas não aguarda a task terminar. O processo pode encerrar antes do `finally`.
6. **UIPI:** se a janela em foreground for elevada, `SendInput` retorna 0 (erro 5). A mensagem atual é genérica.

### I: Configurações e conversores WPF · **Parcialmente confirmado** · Baixa/Média
- **Não confirmado como descrito:** os conversores de enum (`EnumEqualsConverter`, `EnumToVisibilityConverter`) só são usados no modo da substituição, e funcionam (o `ConvertBack` com `false` retorna `DoNothing`). As opções principais **não usam binding**: a `SettingsWindow` faz cópia manual no code-behind.
- **Confirmado:**
  - Substituição do tipo Combinação salva sem tecla capturada (`ComboVirtualKey = 0`) é aceita e envia o scan 0.
  - Não há limites superiores de validação (por exemplo, `CharDelayMs = 100000`).
  - A `SettingsWindow` pode ser aberta e salva **durante** uma sessão. Nesse caso `ReloadSettings` re-registra os atalhos no meio da digitação.
  - Se o `settings.json` estiver corrompido, a janela mostra os padrões sem avisar (ver F).

## 4. Problemas adicionais encontrados

| # | Problema | Onde | Severidade | Status |
|---|---|---|---|---|
| N1 | **Tecla morta literal.** `VkKeyScanEx('~')`, `'^'`, `'´'`, `` '`' ``, `'¨'` (e `'` `"` no US-International) resolvem para a *tecla morta*, que é enviada sozinha. No destino, ela se combina com o próximo caractere (`~a` → `ã`) ou consome um espaço (`~ ` → `~`). O resultado é texto errado (omissão ou substituição) sem nenhum aviso. Precisa ser corrigido enviando tecla morta + `Espaço` quando o caractere desejado for o próprio acento. | `KeyboardMappingService.cs:79-85, 128-142` | Alta | Confirmado no código (efeito real depende do layout da VM) |
| N2 | **O layout do host é tomado como layout da VM.** A resolução usa `GetKeyboardLayout` da janela do navegador no host. Se a VM usar outro layout, os scan codes produzem outros caracteres. Não existe configuração de "layout de destino". | `KeyboardMappingService.cs:71-77`, `KeyboardInputService.cs:91` | Alta | Confirmado no código |
| N3 | **A janela de diagnóstico pode digitar em paralelo com a sessão principal.** Não há trava compartilhada, então duas `TypeTextAsync` podem intercalar eventos no mesmo fluxo de entrada. As duas tentam registrar ESC global, e a segunda falha. `SendTest_Click` é `async void` e não captura exceções genéricas. | `DiagnosticsWindow.xaml.cs` | Alta | Confirmado no código |
| N4 | **Texto sensível em logs.** O modo diagnóstico grava cada caractere (`Char='x'`) em `%AppData%\VmTyper\logs`. Os logs de substituição gravam `De`/`Para`. `queue.json` guarda o texto integral. | `KeyboardInputService.cs:117, 461, 466` | Alta | Confirmado no código |
| N5 | **Intervalos configurados não são respeitados.** `Task.Delay(CharDelayMs)` tem resolução de ~15,6 ms no timer padrão do Windows. Os presets 1, 3 e 10 ms viram ~15,6 ms na prática, ou ~1 ms se outro processo (às vezes o próprio Chrome) elevar a resolução global. O ritmo real depende de processos de terceiros, o que impede reproduzir medições. | `KeyboardInputService.cs:131-135`, `SettingsWindow.xaml.cs` (presets) | Média | Confirmado no código; medição na Fase 0 |
| N6 | **O README afirma uma causa raiz sem evidência.** A seção 0.2 declara "Causa raiz da duplicação: KeyUp perdido". Não há medição que sustente isso, o que contraria a regra do prompt. A seção 2 (árvore) também omite `TypingQueueService`, `QueuePersistenceService` e os conversores. | `README.md` | Média | Confirmado |
| N7 | **Links quebrados no README.** Os links usam `VmTyper/VmTyper/...` a partir da raiz, mas o caminho correto é `VmTyper/...`. | `README.md:9,13,21...` | Baixa | Confirmado |
| N8 | **Substituições dependem da ordem e podem encadear.** Uma substituição de texto `a→b` seguida de `b→c` transforma `a` em `c`. O sentinela de combinação (U+E000+) colide com caracteres reais da Área de Uso Privado presentes no texto. | `KeyboardInputService.cs:442-471` | Baixa | Confirmado no código |
| N9 | **O histórico usa `BlockOrder`, não `Id`.** Depois de reordenar, o histórico aponta para outro bloco. | `TypingSession.cs`, `MainViewModel.cs:420-433` | Baixa | Confirmado |
| N10 | **Atalho Pausar/Continuar durante a contagem é ignorado,** porque `TogglePause` só age em `Digitando`/`Pausado`. Não há opção de pausar antes do primeiro caractere. | `MainViewModel.cs:444-462` | Baixa | Confirmado |
| N11 | **Shift+Home pode selecionar demais em editores de linha única ou com quebra visual** (word-wrap: Home vai ao início da linha *visual*). O resultado é que texto existente acaba substituído. Esse caso não está documentado. | `KeyboardInputService.cs:379-411` | Média | Hipótese (testar) |
| N12 | **`PreciseDelay` faz spin em thread do pool.** Sob carga, a thread pode ser preemptada e a duração KeyDown→KeyUp se estende de forma imprevisível, sem que isso seja medido. | `KeyboardInputService.cs:60-70` | Média | Hipótese (medir) |
| N13 | **`Process.GetProcessById` a cada verificação** e nenhum cache de PID. Isso será relevante quando o TargetGuard verificar a cada caractere. | `ActiveWindowService.cs` | Baixa | Confirmado |

## 5. Avaliação geral

O núcleo é pequeno e bem comentado, e várias decisões de segurança corretas já existem. Os problemas se concentram em três áreas:

1. **Integridade de estado:** fila, persistência e concorrência entre janelas. São os defeitos de maior risco, porque causam perda de dados ou transições erradas *sem aviso*. Prioridade 1.
2. **Fidelidade do envio:** tecla morta literal, layout host≠VM, teclas estendidas, modificadores físicos, liberação parcial. Isso produz texto errado no destino com aparência de sucesso. N1 e H4 são candidatos fortes a explicar parte dos "caracteres errados ou duplicados" relatados, **antes** de qualquer hipótese de rede ou VMware.
3. **Observabilidade e testabilidade:** não há testes, a camada nativa é estática e o ritmo depende da resolução do timer. Sem resolver isso, as Fases 2 e 3 não conseguem produzir evidência reproduzível.

Uma reescrita não se justifica. O plano de fases propõe extrair 4 ou 5 abstrações (injetor, relógio/atraso, armazenamento, mapeamento de layout, coordenador de sessão) e corrigir os defeitos acima atrás de testes.
