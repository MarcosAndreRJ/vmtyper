using System.Diagnostics;
using VmTyper.Helpers;
using VmTyper.Models;
using VmTyper.Native;

namespace VmTyper.Services;

/// <summary>
/// Implementação de referência de IKeyboardInputService baseada em
/// SendInput (P/Invoke), sem qualquer uso de área de transferência.
///
/// Estratégia de confiabilidade (prioridade #1 do projeto):
///  - Para cada caractere lógico do texto existe EXATAMENTE uma decisão de
///    envio: <see cref="SendCharacterAsync"/> resolve um único método
///    (Teclado Físico OU, apenas se o primeiro falhar em modo Automático,
///    Unicode — nunca os dois) e envia uma única vez. Não há caminho de
///    código em que o mesmo caractere seja reenviado por outro método.
///  - Todo KeyDown é seguido do seu KeyUp correspondente em duas chamadas
///    SendInput separadas, com uma pequena folga real (não-cancelável)
///    entre elas — <see cref="KeyPressDurationMs"/>. Isso é proposital:
///    um par down/up perfeitamente simultâneo (mesmo timestamp, mesma
///    chamada) pode não ser distinguido de forma confiável por um pipeline
///    de teclado virtualizado (navegador → console VMware HTML5 → VM), o
///    que pode manifestar-se como uma tecla "presa" cujo KeyUp real só é
///    absorvido pelo próximo KeyDown da mesma tecla mais adiante no texto —
///    visualmente indistinguível de um caractere duplicado.
///  - Um atraso configurável é aplicado ENTRE caracteres (e um atraso maior
///    e específico após Enter), pois consoles VMware HTML5 no navegador
///    podem perder eventos enviados rápido demais.
///  - Em qualquer saída do laço (sucesso, cancelamento ou exceção), um
///    bloco finally garante KeyUp de todos os modificadores, para que
///    nenhuma tecla fique "logicamente pressionada" na sessão do usuário.
///  - A folga entre KeyDown/KeyUp de uma mesma unidade de envio é uma
///    espera SÍNCRONA e não-cancelável (ver <see cref="PreciseDelay"/>):
///    uma vez iniciado, um KeyDown sempre recebe seu KeyUp antes que um
///    cancelamento seja observado — nunca deixamos uma tecla presa por
///    causa de um ESC que chegou no meio de uma combinação. Usamos um
///    spin-wait baseado em Stopwatch, e não Task.Delay, porque a
///    resolução padrão do timer do Windows (~15,6 ms) é muito mais grosseira
///    do que os poucos milissegundos que essa folga precisa — um
///    Task.Delay(2) real pode acabar levando ~15 ms, multiplicando o tempo
///    total de digitação de um texto grande sem necessidade.
/// </summary>
public sealed class KeyboardInputService : IKeyboardInputService
{
    /// <summary>
    /// Folga real e não-cancelável entre KeyDown e KeyUp de uma mesma
    /// tecla/combinação. Ver explicação na documentação da classe.
    /// </summary>
    private const int KeyPressDurationMs = 2;

    /// <summary>
    /// Espera precisa e síncrona por poucos milissegundos, sem depender de
    /// Task.Delay (cuja resolução mínima no Windows normalmente é ~15,6 ms
    /// — muito mais grosseira do que os 1-2 ms que KeyPressDurationMs
    /// precisa). Usa Stopwatch (alta resolução) com um spin-wait leve;
    /// aceitável aqui porque a duração é sempre muito curta e ocorre uma
    /// vez por tecla, não em um laço grande sem pausas.
    /// </summary>
    private static void PreciseDelay(int milliseconds)
    {
        if (milliseconds <= 0) return;

        var sw = Stopwatch.StartNew();
        long targetTicks = (long)(milliseconds * (Stopwatch.Frequency / 1000.0));
        while (sw.ElapsedTicks < targetTicks)
        {
            Thread.SpinWait(100);
        }
    }

    private readonly KeyboardMappingService _mapping;
    private readonly LogService _log;

    public KeyboardInputService(KeyboardMappingService mapping, LogService log)
    {
        _mapping = mapping;
        _log = log;
    }

    public async Task TypeTextAsync(
        string text,
        TypingOptions options,
        IProgress<TypingProgress>? progress,
        PauseToken pauseToken,
        CancellationToken cancellationToken)
    {
        var (normalized, comboSubstitutions) = ApplySubstitutions(NormalizeLineEndings(text), options.Substitutions, _log);
        string[] lines = normalized.Split('\n');
        int total = normalized.Length;
        var layout = _mapping.GetForegroundLayout();
        var stopwatch = Stopwatch.StartNew();
        int sent = 0;
        int charIndex = 0;

        if (options.DiagnosticMode)
        {
            _log.Info($"[Diagnóstico] Início. Total de caracteres: {total}. Linhas: {lines.Length}. Método: {options.Method}. PreservarIndentação: {options.PreserveOriginalIndentation}.");
        }

        async Task SendAndAdvanceAsync(char c)
        {
            // Cada iteração é uma "unidade de envio" completa e atômica do
            // ponto de vista do índice: cancelamento/pausa só são
            // observados ANTES de decidir enviar o próximo caractere, nunca
            // no meio do envio de um já iniciado, e o índice avança
            // exatamente uma vez por caractere efetivamente processado.
            cancellationToken.ThrowIfCancellationRequested();
            await pauseToken.WaitWhilePausedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();

            var outcome = SendCharacter(c, options.Method, layout, comboSubstitutions);
            sent++;

            if (options.DiagnosticMode)
            {
                _log.Info($"[Diagnóstico] Index={charIndex} Unicode=U+{(int)c:X4} Char='{Display(c)}' Método={outcome} KeyDown=1 KeyUp=1");
            }
            charIndex++;

            progress?.Report(new TypingProgress
            {
                TotalCharacters = total,
                TypedCharacters = sent,
                Elapsed = stopwatch.Elapsed,
                CharactersPerSecond = stopwatch.Elapsed.TotalSeconds > 0
                    ? sent / stopwatch.Elapsed.TotalSeconds
                    : 0
            });

            int delay = c == '\n' ? options.EnterDelayMs : options.CharDelayMs;
            if (delay > 0)
            {
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        try
        {
            for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
            {
                if (lineIndex > 0)
                {
                    // Enter (conta como caractere normal do texto original).
                    await SendAndAdvanceAsync('\n').ConfigureAwait(false);

                    // A autoindentação criada pelo editor de destino só pode
                    // existir DEPOIS de um Enter gerado por nós — nunca na
                    // primeira linha do texto, que é digitada exatamente
                    // como está.
                    if (options.PreserveOriginalIndentation)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        await pauseToken.WaitWhilePausedAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                        cancellationToken.ThrowIfCancellationRequested();

                        ResetLineIndentation(options.IndentationStrategy, layout);
                    }
                }

                // A linha inteira (incluindo sua indentação original) é
                // enviada caractere a caractere pelo mesmo caminho normal —
                // não há necessidade de separar "indentação" de "conteúdo"
                // como duas fases distintas: como o cursor já foi
                // reposicionado na coluna correta acima, digitar a linha
                // completa do zero reproduz a indentação original com
                // exatidão, sem risco de um parsing extra de espaços/tabs.
                string line = lines[lineIndex];
                foreach (char c in line)
                {
                    await SendAndAdvanceAsync(c).ConfigureAwait(false);
                }
            }
        }
        finally
        {
            // Rede de segurança final: mesmo que cada unidade de envio já
            // balanceie seus próprios modificadores, garantimos aqui que
            // nada fique preso, cobrindo também exceções inesperadas.
            Win32.ReleaseAllModifiers();
        }
    }

    /// <summary>Resolve e envia um único caractere por exatamente um método. Retorna qual método foi usado, para diagnóstico.</summary>
    private string SendCharacter(char c, InputMethod method, IntPtr layout, IReadOnlyDictionary<char, CharacterSubstitution> comboSubstitutions)
    {
        // Substituições em modo "Combinação de teclas" (recurso adicional):
        // o sentinela marcado por ApplySubstitutions é reconhecido aqui e
        // envia a combinação física exata que o usuário capturou,
        // ignorando por completo a resolução normal de layout/dead-keys —
        // usado quando a VM interpreta um caractere específico de forma
        // diferente mesmo com o modificador certo fisicamente pressionado.
        if (comboSubstitutions.TryGetValue(c, out var sub))
        {
            SendKeyCombo(new KeyCombo((ushort)sub.ComboVirtualKey, sub.ComboShift, sub.ComboCtrl, sub.ComboAlt), layout);
            return $"Combinação ({sub.ComboLabel})";
        }

        if (c == '\n')
        {
            SendSpecialKey(Win32.VK_RETURN, layout);
            return "Enter";
        }

        if (c == '\t')
        {
            SendSpecialKey(Win32.VK_TAB, layout);
            return "Tab";
        }

        if (method == InputMethod.Unicode)
        {
            SendUnicodeChar(c);
            return "Unicode";
        }

        // ResolveMethod: a partir daqui, o caractere é resolvido para NO
        // MÁXIMO um método antes do envio. O modo Automático só tenta
        // Unicode quando o Físico falhou em RESOLVER (não em confirmar
        // entrega — não existe tal confirmação) — ou seja, a escolha é
        // feita ANTES de qualquer envio, nunca como uma segunda tentativa
        // depois de já termos enviado algo pelo primeiro método.
        var mapping = _mapping.Resolve(c, layout);
        if (mapping.Success)
        {
            foreach (var combo in mapping.Combos)
            {
                SendKeyCombo(combo, layout);
            }
            return "Físico";
        }

        if (method == InputMethod.Automatico)
        {
            _log.Warn($"Método automático: fallback para Unicode no caractere U+{(int)c:X4} ('{Display(c)}').");
            SendUnicodeChar(c);
            return "Unicode(fallback)";
        }

        _log.Warn($"Caractere não suportado pelo layout ativo e ignorado: U+{(int)c:X4} ('{Display(c)}'). {mapping.FailureReason}");
        return "Ignorado";
    }

    private static string Display(char c) => c switch
    {
        '\n' => "\\n",
        '\r' => "\\r",
        '\t' => "\\t",
        _ => c.ToString()
    };

    private static (ushort scan, bool extended) ResolveScan(ushort vk, IntPtr layout)
    {
        ushort scan = (ushort)Win32.MapVirtualKeyEx(vk, Win32.MAPVK_VK_TO_VSC, layout);
        return (scan, Win32.IsExtendedKey(vk));
    }

    /// <summary>Envia KeyDown, aguarda uma folga real não-cancelável, depois envia KeyUp — em duas chamadas SendInput separadas.</summary>
    private static void SendScanKey(ushort scan, bool extended)
    {
        Win32.SendInputs(new[] { Win32.ScanKeyDown(scan, extended) });
        PreciseDelay(KeyPressDurationMs);
        Win32.SendInputs(new[] { Win32.ScanKeyUp(scan, extended) });
    }

    private static void SendSpecialKey(ushort vk, IntPtr layout)
    {
        var (scan, extended) = ResolveScan(vk, layout);
        SendScanKey(scan, extended);
    }

    /// <summary>Envia uma tecla com Shift opcional (usada pelas rotinas de reset de indentação).</summary>
    private static void SendModifiedKey(ushort vk, bool shift, IntPtr layout)
    {
        var downs = new List<Win32.INPUT>();
        var ups = new List<Win32.INPUT>();

        if (shift)
        {
            var (ss, se) = ResolveScan(Win32.VK_LSHIFT, layout);
            downs.Add(Win32.ScanKeyDown(ss, se));
            ups.Insert(0, Win32.ScanKeyUp(ss, se));
        }

        var (ms, me) = ResolveScan(vk, layout);
        downs.Add(Win32.ScanKeyDown(ms, me));
        ups.Insert(0, Win32.ScanKeyUp(ms, me));

        Win32.SendInputs(downs);
        PreciseDelay(KeyPressDurationMs);
        Win32.SendInputs(ups);
    }

    private static void SendKeyCombo(KeyCombo combo, IntPtr layout)
    {
        var downs = new List<Win32.INPUT>();
        var ups = new List<Win32.INPUT>();

        // Modificadores também são enviados por Scan Code (não por Virtual
        // Key) para manter consistência com a tecla principal: consoles
        // VMware HTML5 tendem a interpretar eventos de teclado físico
        // (scan code) de forma mais confiável do que eventos sintéticos
        // baseados apenas em Virtual Key.
        void AddModifier(ushort vk)
        {
            var (s, e) = ResolveScan(vk, layout);
            downs.Add(Win32.ScanKeyDown(s, e));
            ups.Insert(0, Win32.ScanKeyUp(s, e));
        }

        if (combo.Shift)
        {
            AddModifier(Win32.VK_LSHIFT);
        }

        if (combo.Ctrl && combo.Alt)
        {
            // AltGr é fisicamente implementado pelo driver de teclado como
            // Ctrl esquerdo + Alt direito pressionados simultaneamente.
            AddModifier(Win32.VK_LCONTROL);
            AddModifier(Win32.VK_RMENU);
        }
        else if (combo.Ctrl)
        {
            AddModifier(Win32.VK_LCONTROL);
        }
        else if (combo.Alt)
        {
            AddModifier(Win32.VK_LMENU);
        }

        var (mainScan, mainExtended) = ResolveScan(combo.VirtualKey, layout);
        downs.Add(Win32.ScanKeyDown(mainScan, mainExtended));
        ups.Insert(0, Win32.ScanKeyUp(mainScan, mainExtended));

        // Todas as teclas descem juntas (ordem correta: modificadores antes
        // da tecla principal), aguardam uma folga real, depois sobem juntas
        // na ordem inversa (tecla principal primeiro, modificadores por
        // último) — duas chamadas SendInput, nunca uma só com down+up.
        Win32.SendInputs(downs);
        PreciseDelay(KeyPressDurationMs);
        Win32.SendInputs(ups);
    }

    private static void SendUnicodeChar(char c)
    {
        Win32.SendInputs(new[] { Win32.UnicodeKeyDown(c) });
        PreciseDelay(KeyPressDurationMs);
        Win32.SendInputs(new[] { Win32.UnicodeKeyUp(c) });
    }

    /// <summary>
    /// Neutraliza a autoindentação inserida pelo editor de destino logo
    /// após um Enter, posicionando/selecionando até a coluna 0 lógica da
    /// nova linha, para que a indentação original do texto possa ser
    /// reaplicada do zero. Executa como unidade atômica e não-cancelável
    /// (nunca interrompida no meio por um ESC) para nunca deixar uma
    /// seleção pendente ou uma tecla presa.
    ///
    /// SEGURANÇA (decisão deliberada): esta rotina NUNCA envia Delete (nem
    /// Backspace) de forma isolada/"às cegas". A estratégia ShiftHomeDelete
    /// apenas SELECIONA (Shift+Home) da posição atual até o início lógico
    /// da linha — a remoção da autoindentação acontece naturalmente quando
    /// os primeiros caracteres da linha original são digitados logo em
    /// seguida (digitar sobre uma seleção ativa a substitui, comportamento
    /// padrão de qualquer caixa de texto/editor Windows). Isso elimina por
    /// construção o risco de um Delete cego apagar conteúdo que já existia
    /// logo após o cursor no documento de destino (ex.: inserindo este
    /// bloco no meio de um arquivo Delphi já existente) — não há mais
    /// nenhum comando de exclusão explícito nesta rotina, então não há como
    /// ela "comer" texto adjacente. No pior caso (uma linha do texto
    /// original é vazia E o editor inseriu autoindentação nela E é a
    /// última linha do bloco inteiro), a seleção fica visualmente aberta
    /// sem ser consumida — um resíduo cosmético, nunca destrutivo. Em
    /// qualquer outro caso (inclusive linha vazia seguida de mais uma
    /// quebra de linha), a seleção é naturalmente substituída pelo próximo
    /// caractere ou Enter enviado.
    /// </summary>
    private static void ResetLineIndentation(IndentationResetStrategy strategy, IntPtr layout)
    {
        var effective = strategy == IndentationResetStrategy.Auto
            ? IndentationResetStrategy.ShiftHomeDelete
            : strategy;

        switch (effective)
        {
            case IndentationResetStrategy.ShiftHomeDelete:
                // Seleciona da posição atual (logo após o Enter, incluindo
                // qualquer autoindentação já inserida pelo editor) até o
                // início lógico da linha. Nenhum Delete é enviado aqui —
                // ver nota de segurança acima.
                SendModifiedKey(Win32.VK_HOME, shift: true, layout);
                break;

            case IndentationResetStrategy.DoubleHome:
                // Estratégia puramente de navegação (sem seleção nem
                // remoção): útil apenas para editores que precisam de duas
                // pressões de Home para alcançar a coluna 0 real (ex.:
                // "smart home") e que não inserem caracteres de
                // autoindentação de fato no buffer, apenas posicionamento
                // visual. Não remove autoindentação real por si só.
                SendModifiedKey(Win32.VK_HOME, shift: false, layout);
                PreciseDelay(KeyPressDurationMs);
                SendModifiedKey(Win32.VK_HOME, shift: false, layout);
                break;

            case IndentationResetStrategy.Disabled:
            default:
                break;
        }
    }

    /// <summary>
    /// Normaliza todas as variantes de quebra de linha (\r\n, \r, \n) para
    /// \n, garantindo que cada quebra resulte em exatamente um Enter.
    /// </summary>
    public static string NormalizeLineEndings(string text)
    {
        return text.Replace("\r\n", "\n").Replace("\r", "\n");
    }

    /// <summary>
    /// Aplica a tabela de substituição manual de caracteres (Configurações
    /// → Substituições) ao texto inteiro, antes de qualquer outro
    /// processamento. Usada para contornar teclas que a VM de destino
    /// digita erradas por causa de um layout de teclado desconfigurado —
    /// o usuário mapeia manualmente o símbolo problemático para um
    /// equivalente que funciona. Aplicada nesta única etapa central, então
    /// vale igualmente para os modos Automático, Teclado Físico e Unicode.
    /// Entradas com "De" vazio são ignoradas (evita substituir a string
    /// vazia, que inseriria "Para" entre cada caractere do texto).
    ///
    /// Substituições em modo <see cref="SubstitutionMode.Combinacao"/>
    /// (recurso adicional) não podem ser resolvidas por uma simples troca
    /// textual, pois precisam ignorar a resolução normal de layout — em vez
    /// disso, cada ocorrência de "De" é trocada por um caractere sentinela
    /// exclusivo (da Área de Uso Privado do Unicode, U+E000+, que não
    /// ocorre em texto real), e o mapa sentinela→substituição é devolvido
    /// para <see cref="SendCharacter"/> reconhecer e enviar a combinação
    /// física exata em vez de tentar digitar o sentinela como texto.
    /// </summary>
    private static (string text, IReadOnlyDictionary<char, CharacterSubstitution> comboMap) ApplySubstitutions(
        string text, IReadOnlyList<CharacterSubstitution> substitutions, LogService log)
    {
        var comboMap = new Dictionary<char, CharacterSubstitution>();
        if (substitutions.Count == 0) return (text, comboMap);

        string result = text;
        char nextSentinel = (char)0xE000; // Area de Uso Privado do Unicode

        foreach (var sub in substitutions)
        {
            if (string.IsNullOrEmpty(sub.From)) continue;
            if (!result.Contains(sub.From, StringComparison.Ordinal)) continue;

            if (sub.Mode == SubstitutionMode.Combinacao)
            {
                char sentinel = nextSentinel++;
                comboMap[sentinel] = sub;
                result = result.Replace(sub.From, sentinel.ToString());
                log.Info($"Substituição (combinação de teclas) aplicada: '{sub.From}' → {sub.ComboLabel}.");
            }
            else
            {
                result = result.Replace(sub.From, sub.To);
                log.Info($"Substituição aplicada: '{sub.From}' → '{sub.To}'.");
            }
        }

        return (result, comboMap);
    }
}
