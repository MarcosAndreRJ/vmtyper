# VmTyper

Aplicativo desktop Windows (C# / .NET 8 / WPF) para digitar textos longos, com acentuação em português brasileiro, dentro de uma VM VMware acessada por um console HTML5 no navegador — **sem usar área de transferência**. Cada caractere é convertido em eventos reais de teclado (`SendInput`, via P/Invoke) e enviado ao processo que estiver em primeiro plano no host (o navegador), que os encaminha ao console VMware e, de lá, à VM.

O aplicativo trabalha com uma **fila de blocos de texto**: cada acionamento do botão DIGITAR (ou do atalho `Ctrl+Shift+Enter`) digita exatamente um bloco e avança para o próximo somente após sucesso — permitindo preparar várias entradas de antemão e percorrer vários campos dentro da VM sem voltar ao VmTyper entre uma e outra (seção 0.1 abaixo).

## 0.2 Correções: caracteres duplicados e indentação acumulativa (código Delphi)

**Causa raiz da duplicação:** `KeyDown` e `KeyUp` de cada tecla eram enviados em uma única chamada `SendInput`, com timestamp zero e **nenhuma separação temporal real** entre eles. Pipelines de teclado virtualizado (navegador → console VMware HTML5 → VM) podem não distinguir de forma confiável um par down/up perfeitamente simultâneo; se o `KeyUp` for "perdido" nessa interpretação, a tecla fica logicamente presa até o próximo `KeyDown` da mesma tecla — visualmente indistinguível de um caractere duplicado mais adiante no texto. Correção: `KeyDown` e `KeyUp` agora são duas chamadas `SendInput` separadas, com uma folga real de ~2 ms (`KeyPressDurationMs`, constante interna) entre elas — **não-cancelável**, então um ESC no meio nunca deixa uma tecla presa. Essa folga usa um spin-wait baseado em `Stopwatch` em vez de `Task.Delay`, porque a resolução padrão do timer do Windows (~15,6 ms) arredondaria 2 ms para cima e tornaria a digitação de textos longos visivelmente mais lenta sem necessidade. Também foi reforçada a proteção de reentrância em [`MainViewModel.cs`](VmTyper/VmTyper/ViewModels/MainViewModel.cs) com uma trava atômica (`Interlocked.CompareExchange`) explícita, além do guard de estado já existente.

**Causa raiz da indentação acumulativa:** o VmTyper enviava `\n` e depois os espaços originais da próxima linha, sem nunca neutralizar a autoindentação que o próprio Delphi insere automaticamente após o Enter — os espaços do editor e os do texto original se somavam a cada linha.

**Correção:** a digitação passou a ser processada por linha em [`KeyboardInputService.cs`](VmTyper/VmTyper/Services/KeyboardInputService.cs). Com a nova opção **Configurações → Digitação → Preservar indentação original** (desativada por padrão), após cada Enter gerado pelo próprio VmTyper (nunca na primeira linha do bloco) é enviado `Shift+Home` para *selecionar* do cursor até o início lógico da linha — e a indentação original do texto é digitada por cima, substituindo naturalmente a seleção (comportamento padrão de qualquer caixa de texto Windows ao digitar sobre uma seleção ativa).

Essa é uma mudança deliberada em relação à ideia original de "`Shift+Home` seguido de `Delete`": um `Delete` às cegas, quando a linha não tinha autoindentação alguma para remover (seleção vazia) e havia conteúdo real logo após o cursor no documento de destino (ex.: inserindo este bloco no meio de um arquivo Delphi já existente), apagaria esse conteúdo. Ao eliminar o comando de exclusão explícito — deixando que a própria digitação substitua a seleção — esse risco desaparece por construção. Validado com um teste automatizado que insere um bloco de código exatamente nessa situação (texto pré-existente logo após o ponto de inserção) e confirma que nada é apagado.

## 0.3 Substituição manual de caracteres

Em **Configurações → Substituições**, é possível cadastrar pares "De" → "Para": sempre que o texto contiver o símbolo "De", o VmTyper digita "Para" no lugar. Serve para contornar teclas que a VM de destino digita erradas por causa de um layout de teclado desconfigurado — o usuário mapeia manualmente os símbolos que já identificou como problemáticos.

- Modelo: [`Models/CharacterSubstitution.cs`](VmTyper/VmTyper/Models/CharacterSubstitution.cs) (`From`/`To`, ambos strings — não precisa ser 1 caractere só; "Para" vazio remove o símbolo do texto).
- Aplicada em um único ponto central, `ApplySubstitutions` em [`KeyboardInputService.cs`](VmTyper/VmTyper/Services/KeyboardInputService.cs), sobre o texto inteiro antes de qualquer outro processamento — vale para os três métodos de digitação (Automático, Teclado Físico, Unicode) igualmente.
- Persistida em `AppSettings.CharacterSubstitutions` (mesmo `settings.json`), preservada entre execuções.
- Validado de ponta a ponta: texto com `@` e `~` cadastrados para virar `(arroba)` e `TIL` respectivamente, confirmando substituição exata caractere por caractere no destino.

**Nota técnica:** ao implementar isso, os enums `Method`/`ExpectedBrowser` em `AppSettings` passaram a ser serializados como texto (`"Automatico"`, `"Chrome"`) em vez de número — mais legível para depuração manual do `settings.json`, e mesmo esquema já usado pela fila de blocos (`TextBlockStatus`). Compatível com arquivos antigos (a leitura aceita tanto o texto quanto o número).

### Recurso adicional: substituição por combinação de teclas física

Além do modo texto (De → Para, digitado pelo caminho normal), cada substituição pode usar o modo **Combinação de teclas**: em vez de digitar um texto, o VmTyper envia uma combinação física exata (modificadores + tecla) capturada pelo botão "Capturar" — igual à captura de atalhos, mas sem exigir modificador (a tecla problemática pode ser justamente uma sem modificador nenhum).

Criado para o caso em que a VM interpreta um modificador de forma inconsistente dependendo da tecla física: por exemplo, `Shift + /` do teclado principal produz um símbolo, mas a VM só reconhece corretamente `Shift + /` do **numpad** para produzir outro. Como a resolução normal por `VkKeyScanEx` não distingue essa nuance (mapeia "?" para uma tecla física específica do layout do host, não necessariamente a do numpad), o modo Combinação contorna isso enviando a tecla física exata, ignorando completamente a resolução de layout.

- Modelo: `Models/CharacterSubstitution.cs` ganhou `Mode` (`Texto`/`Combinacao`) + campos `Combo*` (modificadores, Virtual Key, rótulo amigável). Recurso puramente aditivo — o modo Texto continua funcionando exatamente como antes.
- Mecanismo: `ApplySubstitutions` em `KeyboardInputService.cs` marca cada ocorrência de uma substituição em modo Combinação com um caractere sentinela exclusivo (Área de Uso Privado do Unicode, U+E000+, que não ocorre em texto real); `SendCharacter` reconhece o sentinela e despacha a combinação física exata via o mesmo mecanismo de envio (`SendKeyCombo`) já usado para caracteres normais — mesma folga KeyDown/KeyUp, mesma liberação garantida de modificadores.
- UI: nomes amigáveis distinguem explicitamente teclas do numpad ("/ (numpad)") das equivalentes no teclado principal ("/ ?"), usando os nomes do enum `Key` do WPF na captura.
- Validado de ponta a ponta: substituição de `?` capturada como `Shift + / (numpad)` foi corretamente identificada e despachada (confirmado pelo log: "Substituição (combinação de teclas) aplicada"), coexistindo sem interferência com uma substituição em modo Texto na mesma execução.

## 0.1 Fila de blocos de texto

Evolução sobre a versão original de texto único, preservando toda a infraestrutura de digitação (`SendInput`, mapeamento de teclado, hotkeys, pausa, cancelamento) sem alterá-la.

- **[`Models/TextBlockItem.cs`](VmTyper/Models/TextBlockItem.cs)** — um bloco (`Id` estável em `Guid`, `Order` visual, `Text`, `Status`). A identidade (`Id`) nunca muda; a posição (`Order`) muda ao reordenar — isso é o que permite histórico, persistência e exclusão continuarem corretos depois de mover blocos.
- **[`Models/TextBlockStatus.cs`](VmTyper/Models/TextBlockStatus.cs)** — `Pending` / `Current` / `Typing` / `Completed`.
- **[`Services/TypingQueueService.cs`](VmTyper/Services/TypingQueueService.cs)** — único ponto responsável pelas regras da fila (avançar, cancelar sem avançar, pular blocos vazios, reordenar, remover, definir bloco manualmente, reiniciar). A função `RecalculateStatuses()` é a única que decide qual bloco é `Current`: percorre a fila em ordem, ignora blocos `Completed`, pula blocos vazios, e atribui `Current` ao primeiro elegível — determinística, sem índice solto espalhado pelo código.
- **[`Services/QueuePersistenceService.cs`](VmTyper/Services/QueuePersistenceService.cs)** — persiste a fila em `%AppData%\VmTyper\queue.json` (autosave com debounce de 1,5s nas edições de texto + gravação imediata em qualquer mutação estrutural — adicionar/remover/mover/concluir/cancelar — + flush final ao fechar o app). Um bloco que estava `Typing` quando o processo foi fechado (ou travou) é restaurado como `Pending`/`Current` na próxima abertura, nunca como "ainda digitando".
- **Concorrência:** o guard `if (IsBusy) return;`, executado de forma síncrona antes do primeiro `await` em `MainViewModel.StartAsync()`, é suficiente para impedir acionamento duplo (botão + hotkey, ou hotkey repetido) — a UI do WPF roda em uma única thread, então não há como duas chamadas se intercalarem entre a checagem e a mudança de estado. Não foi necessário `SemaphoreSlim` adicional.
- **Cancelamento nunca avança a fila.** `MainViewModel` chama `_queueService.CancelBlock(id)` tanto no `catch (OperationCanceledException)` quanto no `catch (Exception)` — o bloco volta a `Pending` e `RecalculateStatuses()` o torna `Current` de novo (mesma posição), garantindo que a próxima execução redigite o bloco inteiro desde o início.
- **Interface:** a área de texto única virou um `ItemsControl` com `DataTemplate` por bloco ([`MainWindow.xaml`](VmTyper/MainWindow.xaml)), dentro de um `ScrollViewer`. Toda a região é desabilitada (`IsEnabled="{Binding IsBusy, ...}"`) durante qualquer execução — simplificação deliberada permitida pelo pedido, em vez de bloquear cada bloco individualmente.

Validado com um teste automatizado ponta-a-ponta (UI Automation do Windows dirigindo o app real) cobrindo: execução sequencial de blocos distintos, bloco vazio pulado automaticamente, cancelamento no meio de um bloco longo sem marcá-lo concluído nem avançar a fila, e desabilitação do botão DIGITAR ao concluir o último bloco (sem loop de volta ao primeiro). Persistência entre reinicializações também foi verificada (fila concluída restaurada corretamente após fechar/reabrir o processo).

## 1. Decisões técnicas importantes

- **Sem Ctrl+V, em nenhum momento.** Todo o texto é decomposto caractere a caractere e enviado como pressionar/soltar teclas.
- **`SendInput` centralizado em [`Native/Win32.cs`](VmTyper/Native/Win32.cs).** Nenhuma outra classe do projeto contém `[DllImport]`.
- **Layout de teclado dinâmico, não hardcoded para ABNT2.** [`KeyboardMappingService`](VmTyper/Services/KeyboardMappingService.cs) usa `GetKeyboardLayout` da janela em primeiro plano (o navegador) + `VkKeyScanEx` para descobrir, em tempo real, qual tecla física + modificador produz cada caractere no layout realmente ativo no Windows do host.
- **Teclas mortas (dead keys) tratadas explicitamente.** No ABNT2, `á à ã â é ê í ó ô õ ú` **não** são teclas diretas — são compostas por "tecla de acento" + letra. `VkKeyScanEx` não resolve isso diretamente (retorna -1). A solução: normalizamos o caractere Unicode para a forma NFD (`"á".Normalize(FormD)` → `'a' + U+0301`), mapeamos a marca combinante para o caractere de acento correspondente (´ ` ^ ~ ¨) e enviamos duas teclas em sequência — exatamente como uma pessoa digitaria fisicamente. `ç`, por ter tecla própria no ABNT2, resolve direto por `VkKeyScanEx` sem precisar de decomposição.
- **Scan Codes, não apenas Virtual Keys.** A tecla principal *e* os modificadores (Shift/Ctrl/AltGr) são enviados via `KEYEVENTF_SCANCODE` (não `KEYEVENTF_VIRTUALKEY`), porque consoles VMware HTML5 tendem a escutar eventos de teclado físico do navegador — scan code é o que mais se aproxima disso (ver item 32 do escopo original).
- **AltGr = Ctrl esquerdo + Alt direito.** Reproduz fielmente como o driver de teclado do Windows sintetiza fisicamente a tecla AltGr, permitindo símbolos como `@ # $ ¨` no ABNT2.
- **Modo Unicode (`KEYEVENTF_UNICODE`) existe, mas é o modo secundário.** Ignora o layout e injeta o code point diretamente; é mais simples, porém alguns consoles VMware HTML5, que capturam apenas eventos de tecla física, podem não interpretá-lo corretamente. Por isso o modo **Automático** usa Teclado Físico como principal e só cai para Unicode nos caracteres que o layout ativo realmente não consegue produzir (registrando isso no log).
- **Liberação garantida de modificadores.** Cada combinação de tecla é auto-contida (down→down→up→up balanceado) e, adicionalmente, um `finally` no nível do laço principal força `KeyUp` de Shift/Ctrl/Alt (esquerdo e direito) ao final — sucesso, cancelamento ou exceção. Validado por teste automatizado (seção 6).
- **ESC como hotkey global, mas só durante a sessão.** `RegisterHotKey(ESC)` é registrado no início da contagem regressiva e desregistrado assim que a digitação termina/cancela — evita capturar ESC globalmente o tempo todo (o que atrapalharia o uso normal do Windows quando o app está ocioso), mas garante cancelamento imediato tanto durante a contagem quanto durante a digitação.
- **Não rouba foco.** A janela de hotkeys é uma `HWND_MESSAGE` invisível (não é uma janela top-level, não pode ganhar foco). Nenhum código chama `Activate()`, `Show()` da janela principal ou `MessageBox` durante o fluxo de digitação — todo o feedback (contagem, progresso, erros de validação de janela) é escrito em propriedades bindadas da própria janela, que continua em segundo plano.
- **Não bloqueia a UI.** `KeyboardInputService.TypeTextAsync` é assíncrono, com `ConfigureAwait(false)` no laço quente (não precisa voltar à UI thread a cada caractere) e reporta progresso via `IProgress<T>`, que faz o marshaling de volta à UI thread automaticamente.
- **Pausar/Continuar sem perder posição.** Implementado com um par `PauseTokenSource`/`PauseToken` (mesmo padrão de `CancellationTokenSource`/`CancellationToken`), sem heurísticas de estado paralelas.

## 2. Arquitetura

```
VmTyper/
├── VmTyper.sln
└── VmTyper/
    ├── VmTyper.csproj          (net8.0-windows, WPF + WinForms p/ tray icon)
    ├── app.manifest
    ├── App.xaml / App.xaml.cs           → composition root (sem DI framework)
    ├── MainWindow.xaml / .xaml.cs       → tela principal
    ├── Views/
    │   ├── SettingsWindow.xaml(.cs)     → configurações
    │   └── DiagnosticsWindow.xaml(.cs)  → "Teste de Teclado"
    ├── ViewModels/
    │   └── MainViewModel.cs             → orquestra contagem/digitação/pausa/cancelamento
    ├── Services/
    │   ├── IKeyboardInputService.cs     → abstração (independe de UI)
    │   ├── KeyboardInputService.cs      → implementação SendInput
    │   ├── KeyboardMappingService.cs    → char → VK+modificadores (com dead keys)
    │   ├── GlobalHotkeyService.cs       → RegisterHotKey via janela HWND_MESSAGE
    │   ├── ActiveWindowService.cs       → valida navegador em primeiro plano
    │   ├── SettingsService.cs           → settings.json em %AppData%\VmTyper
    │   ├── HistoryService.cs            → history.json (metadados, nunca o texto)
    │   ├── LogService.cs                → log técnico em memória + arquivo
    │   └── TrayIconService.cs           → ícone na bandeja
    ├── Models/
    │   ├── AppSettings.cs, TypingOptions.cs, TypingProgress.cs,
    │   │   TypingState.cs, TypingSession.cs
    ├── Native/
    │   └── Win32.cs              → único ponto de P/Invoke do projeto
    └── Helpers/
        ├── ObservableObject.cs, RelayCommand.cs, PauseToken.cs,
        │   KeyNameHelper.cs, InverseBoolConverter.cs
```

`IKeyboardInputService` é a fronteira entre UI e mecanismo de digitação: `MainViewModel` e `DiagnosticsWindow` dependem apenas dela, nunca de `Win32` diretamente.

## 3. Compilar

**Linha de comando** (com .NET 8 SDK instalado):

```bash
cd VmTyper
dotnet build VmTyper.sln -c Release
```

O executável fica em `VmTyper\bin\x64\Release\net8.0-windows\VmTyper.exe`.

**Visual Studio 2022:**
1. Abra `VmTyper.sln`.
2. Confirme que o SDK do .NET 8 está instalado (`Visual Studio Installer` → *Individual components* → *.NET 8.0 Runtime*).
3. Defina a plataforma de solução como `x64` (já é o padrão do projeto).
4. `Ctrl+Shift+B` para compilar, `F5` para rodar em modo Debug.

Build já verificado neste ambiente: **compilação limpa, 0 erros, 0 avisos**, tanto em `Debug` quanto em `Release`.

## 4. Testar localmente (antes da VM) — fluxo do item 28

1. Rode `VmTyper.exe`.
2. Abra o Bloco de Notas do Windows (ou qualquer editor de texto).
3. Cole um parágrafo com acentuação na caixa de texto do VmTyper (ex.: os 7 testes da seção 27 do escopo).
4. Clique em **DIGITAR**.
5. Durante os "3... 2... 1...", clique dentro do Bloco de Notas.
6. O texto deve aparecer integralmente, caractere por caractere.
7. Repita e, no meio da digitação, pressione **ESC** — a digitação deve parar imediatamente e a barra de status deve mostrar "Digitação cancelada pelo usuário."

### Validação automatizada já executada

Como parte da revisão final deste projeto, dois testes ponta-a-ponta foram executados de fato (via UI Automation do Windows, sem intervenção manual), confirmando o comportamento real do `SendInput`:

- **Teste de digitação completa:** texto com `ação, café, início, avó, órgão, coração`, `R$ 1.250,00`, símbolos (`! @ # $ % & * ( ) - _ = +`), tab e linha em branco → **recebido de forma idêntica**, caractere por caractere, na janela de destino.
- **Teste de cancelamento:** texto longo em maiúsculas (força Shift quase o tempo todo) → ESC enviado no meio da digitação → digitação parou imediatamente, **nenhum caractere a mais** foi recebido depois, e `GetAsyncKeyState` confirmou que **nenhum modificador (Shift/Ctrl/Alt) ficou fisicamente preso**.

## 5. Testar dentro da VM VMware pelo navegador

1. Abra a sessão VMware no navegador normalmente.
2. Dentro da VM, abra o Bloco de Notas (ou o campo de destino) e deixe-o com foco.
3. No VmTyper (rodando no host, fora da VM), cole o texto e clique **DIGITAR**, ou use o atalho global `Ctrl+Shift+Enter` configurado em **Configurações → Atalhos**.
4. Durante a contagem regressiva, clique dentro da VM (no navegador) para garantir que o navegador está em primeiro plano.
5. Se o texto não aparecer corretamente:
   - Vá em **Configurações → Digitação** e compare os métodos **Automático**, **Teclado físico** e **Unicode** — nem todo console VMware HTML5 aceita bem `KEYEVENTF_UNICODE`, e o inverso também pode ocorrer dependendo da build do console.
   - Aumente os intervalos (**Intervalo entre caracteres** / **Intervalo após Enter**) usando o preset **Compatibilidade** — consoles HTML5 sob carga podem perder eventos enviados rápido demais.
   - Use **Teste de Teclado** para isolar quais caracteres especificamente falham, sem precisar reenviar o texto inteiro.
   - Se o navegador estiver rodando **elevado** ("Executar como administrador") e o VmTyper não, o Windows bloqueia a injeção de teclado por UIPI — nesse caso, rode o VmTyper também como administrador.
6. Ative **Segurança → Validar navegador de destino** para reduzir o risco de digitar na janela errada (o Notepad da VM não é uma janela real do host, então a validação é feita sobre o processo do navegador — Chrome/Edge/Firefox).

## 6. Revisão final (compilação, concorrência, foco, mapeamento, cancelamento)

Problemas encontrados e corrigidos durante a revisão:

1. **Ambiguidade de tipos** (`Application`, `MessageBox`, `KeyEventArgs`, `InputMethod`) causada por `UseWindowsForms=true` (necessário para o ícone de bandeja) coexistindo com WPF — resolvida com `using` totalmente qualificados/aliases.
2. **ESC não funcionava durante a contagem regressiva** — o hotkey de cancelamento só era registrado depois dela. Corrigido: ESC agora é registrado assim que o usuário clica em DIGITAR, cobrindo contagem + digitação + pausa.
3. **Inconsistência Scan Code vs. Virtual Key** — os modificadores (Shift/Ctrl/AltGr) eram enviados por Virtual Key enquanto a tecla principal usava Scan Code. Padronizado: tudo é enviado por Scan Code, para máxima compatibilidade com o console VMware.
4. **Aviso de DPI** (WFAC010) por configuração duplicada entre `app.manifest` e a propriedade WinForms — resolvido movendo a configuração de DPI para `ApplicationHighDpiMode` no `.csproj`.

Após as correções: build limpo (0 erros/0 avisos) e os dois testes ponta-a-ponta descritos na seção 4 passaram.
