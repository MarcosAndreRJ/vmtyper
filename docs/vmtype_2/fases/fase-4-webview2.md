# Fase 4: Navegador integrado com WebView2 (experimental)

**Objetivo:** avaliar, com uma prova de conceito, se embutir o navegador traz ganhos de **observabilidade, controle de foco e confiabilidade**, e comparar **SendInput** com **CDP `Input.dispatchKeyEvent`**. O modo de navegador externo **não é substituído**.

**Pré-requisitos:** F1–F3 (executor, scheduler, diagnóstico e página HTML de diagnóstico, que servirá de alvo da PoC).

**Feature flags:** `EmbeddedBrowser` (padrão: desligada) e `CdpInput` (padrão: desligada).

---

## §1 PoC isolada (`experiments/WebView2Poc/`)
Um projeto WPF separado, fora do app, para não desestabilizar o principal.

1. Pacote `Microsoft.Web.WebView2` (versão estável fixada no `Directory.Packages.props`). Antes de escrever código, **consultar a documentação oficial** da versão escolhida e registrar no relatório os links e versões usados.
2. **Inicialização:**
   - Antes de criar o controle, `CoreWebView2Environment.GetAvailableBrowserVersionString()`. Se o runtime estiver ausente, mostrar uma mensagem clara (sem instalar nada sozinho).
   - `CoreWebView2Environment.CreateAsync(userDataFolder: %LocalAppData%\VmTyper\WebView2Profile, options)`.
   - `await webView.EnsureCoreWebView2Async(env)`.
3. **Navegação:** barra de endereço, Voltar, Avançar, Atualizar, indicador de carregamento (`NavigationStarting`/`NavigationCompleted`) e erros (`WebErrorStatus` → mensagem amigável).
4. **Página-alvo:** a página HTML de diagnóstico da F3 §2, carregada localmente (`SetVirtualHostNameToFolderMapping`).
5. **Método A (SendInput):** focar o WebView2 (`webView.Focus()` e `CoreWebView2Controller.MoveFocus`) e usar o `TypingScheduler` existente. A página registra os eventos.
6. **Método B (CDP):** ver §3.
7. **Comparação:** o mesmo corpus pelos dois métodos → JSON exportado pela página → o classificador da F3 compara `key`/`code`/`repeat`/`isTrusted`/ordem/timestamps e o texto final.
8. **Sessão VMware real** (somente com acesso autorizado e uma pessoa presente): repetir o passo 7 com o receptor na VM (F3 §3).
9. Relatório `relatorios/F4-poc-webview2.md`.

## §2 Perfil, autenticação e rede
| Tema | Abordagem | Limite |
|---|---|---|
| Persistência de sessão e cookies | `userDataFolder` próprio e persistente | Cookies de outros navegadores **não** são copiados |
| SSO / autenticação integrada | Avaliar `CoreWebView2EnvironmentOptions.AllowSingleSignOnUsingOSPrimaryAccount` e o comportamento padrão para autenticação integrada do Windows | Se o provedor de identidade bloquear navegadores embutidos, registrar e **não** contornar |
| Certificados de cliente | Evento `ClientCertificateRequested`: deixar o usuário escolher, sem seleção automática silenciosa | |
| Certificados de servidor inválidos | Nunca ignorar `ServerCertificateErrorDetected` automaticamente | |
| Proxy e políticas | O WebView2 respeita as políticas do Edge e do sistema. Registrar o comportamento observado | |
| Credenciais | O VmTyper **nunca** digita nem armazena senhas. O login é feito pela pessoa | |

## §3 Método CDP: `Input.dispatchKeyEvent`
- **Chamada:** `await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent", json)`.
- **Parâmetros a mapear** (confirmar na documentação oficial do protocolo antes de implementar):
  - `type`: `keyDown` / `keyUp` / `rawKeyDown` / `char`;
  - `key`, `code`, `windowsVirtualKeyCode`, `nativeVirtualKeyCode`;
  - `text` / `unmodifiedText`;
  - `modifiers` (bitmask: Alt, Ctrl, Meta, Shift);
  - `autoRepeat`, `isKeypad`, `location`.
- **Adaptador `CdpKeyEventMapper`:** recebe as unidades do `KeyStrokePlanner` e produz eventos CDP com `code` derivado do **scan code** (tabela scan→`KeyboardEvent.code` da UI Events KeyboardEvent code spec), `key` derivado do layout de destino e `text` para os imprimíveis.
- **Teclas mortas no CDP:** avaliar duas abordagens: enviar `key: "Dead"` seguido da letra, ou enviar diretamente o caractere composto em `text`. **Não presumir** qual o console aceita. Testar as duas.
- **Implementar `IInputInjector` para o CDP** (ou um executor paralelo), para que o scheduler, a política, as métricas e o diagnóstico sejam **os mesmos** nos dois métodos.
- **Hipótese a verificar:** consoles como o VMware HTML5 normalmente traduzem `code` para scan codes do guest. Se isso se confirmar, o CDP pode oferecer controle determinístico de `code`, independentemente do foco da janela Win32. Isso **não** pode ser presumido: o relatório informa o que foi observado.

## §4 Controle de foco no navegador integrado
| Nível | Como verificar | Confiável? |
|---|---|---|
| Janela do VmTyper em foreground | `GetForegroundWindow` | Sim |
| WebView2 com foco | `GotFocus`/`LostFocus` do controle, `CoreWebView2Controller` | Sim |
| Documento com foco | `ExecuteScriptAsync("document.hasFocus()")` | Sim (frame principal) |
| Elemento ativo | `document.activeElement` (tag/id) | Só na mesma origem |
| Console VMware com foco | Canvas/elemento do console ativo (seletor configurável) | Heurístico |
| VM pronta para receber | **Não verificável** pelo host | Não. Exibir "desconhecido" |

O `TargetGuard` ganha um modo `Embedded`, que verifica os níveis disponíveis antes de cada unidade e pausa em qualquer perda. Um nível que não pode ser verificado aparece como "desconhecido", nunca como "ok".

## §5 Integração experimental no app (só se a PoC for viável)
1. Uma nova janela `Views/EmbeddedBrowserWindow.xaml` (ou uma aba). Os controles do VmTyper ficam numa barra **separada** do WebView2: não se sobrepõem, por causa do airspace do HWND.
2. Seleção de ambiente (F5): Externo (padrão) | Integrado + SendInput | Integrado + CDP (experimental).
3. A fila, o scheduler, o diagnóstico e as métricas são compartilhados. Só o injetor e o guard mudam.
4. **Falhas do WebView2** (`ProcessFailed`) → pausa a sessão, oferece recarregar e nunca continua injetando.
5. Com a flag desligada: nenhum código do WebView2 é carregado (inicialização sob demanda). O app continua funcionando sem o runtime.

## Critérios de aceite da F4
- [ ] PoC: inicialização, navegação, página local, SendInput e CDP testados, com resultados no relatório.
- [ ] Viabilidade do CDP **demonstrada ou descartada com justificativa** (dados da página de diagnóstico e, se houver acesso, da VM).
- [ ] Autenticação corporativa testada pela pessoa, ou registrada como limitação.
- [ ] Modo externo: suíte da F1–F3 verde e sem nenhuma dependência nova obrigatória.
- [ ] Se SendInput e CDP forem equivalentes, recomendar o de menor complexidade (provavelmente o SendInput, que já está validado). Se o integrado for instável, ele continua experimental e desligado.
