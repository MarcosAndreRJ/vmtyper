# ADR 0009: Avaliação do Navegador Integrado (WebView2) e CDP `Input.dispatchKeyEvent` vs `SendInput`

- **Data:** 2026-11-09
- **Status:** Aceito (Fase 4)
- **Contexto:** A Fase 4 (§1–§5) exige avaliar com uma prova de conceito isolada (`experiments/WebView2Poc/`) se embutir o navegador via `Microsoft.Web.WebView2` (`1.0.4314.35`) e enviar teclas via Chrome DevTools Protocol (`Input.dispatchKeyEvent`) ou `SendInput` traz ganhos reais de observabilidade, controle de foco e confiabilidade em relação ao modo de navegador externo, e recomendar a arquitetura de menor complexidade caso sejam equivalentes.

## Documentação Oficial Consultada e Versões Fixadas

- **Pacote NuGet fixado em `Directory.Packages.props`:** `Microsoft.Web.WebView2` versão **`1.0.4314.35`** (somente referenciado pelo projeto experimental `experiments/WebView2Poc/WebView2Poc.csproj`; o projeto principal `VmTyper.csproj` mantém zero dependência obrigatória do WebView2 Runtime).
- **Documentação oficial Microsoft Learn (`CoreWebView2`):**
  - `CoreWebView2.SetVirtualHostNameToFolderMapping`: https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping
  - `CoreWebView2.CallDevToolsProtocolMethodAsync`: https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2.calldevtoolsprotocolmethodasync
  - `CoreWebView2Environment.GetAvailableBrowserVersionString`: https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2environment.getavailablebrowserversionstring
  - `CoreWebView2EnvironmentOptions.AllowSingleSignOnUsingOSPrimaryAccount`: https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2environmentoptions.allowsinglesignonusingosprimaryaccount
- **Especificação Chrome DevTools Protocol (`Input.dispatchKeyEvent`):**
  - https://chromedevtools.github.io/devtools-protocol/tot/Input/#method-dispatchKeyEvent

## Resultados Observados na PoC (`experiments/WebView2Poc`)

1. **Observabilidade e Controle de Foco (§4):**
   - No documento principal (mesma origem, ex.: `https://diag.vmtyper.local/index.html`), `ExecuteScriptAsync("document.hasFocus()")` e `document.activeElement` funcionam de forma determinística.
   - Contudo, conforme previsto na matriz do §4, **não é possível inspecionar iframes cross-origin** nem saber se o sistema convidado dentro do console VMware está pronto para receber teclas (`GuestReadyStatus` permanece sempre `"desconhecido"`).
2. **Comparação Técnica `SendInput` (Método A) vs `CDP Input.dispatchKeyEvent` (Método B1 `DeadKeyThenComposed` e Método B2 `ComposedTextDirect`):**
   - **`isTrusted`:** tanto `SendInput` quanto `CDP Input.dispatchKeyEvent` geram eventos DOM com `isTrusted: true` no Chromium/WebView2 (diferentemente de `dispatchEvent(new KeyboardEvent(...))` via JavaScript).
   - **Foco da janela Win32:** o `CDP Input.dispatchKeyEvent` entrega eventos diretamente ao `RenderWidgetHost` do WebView2 **mesmo quando a janela Win32 não está em foreground no SO** (`GetForegroundWindow() == 0x0`), enquanto `SendInput` exige estritamente foco de janela Win32 ativo.
   - **Semântica de teclas mortas e `KeyboardEvent.code` (§3):**
     - No CDP, o parâmetro `code` (ex.: `"KeyA"`, `"Quote"`, `"BracketLeft"`, `"IntlRo"`, `"Semicolon"`) é populado explicitamente pelo `CdpKeyEventMapper` a partir do scan code Set 1, enquanto a inserção textual em elementos HTML (`<textarea>`) depende do campo `text` do evento `keyDown`/`char` (pois o `Input.dispatchKeyEvent` do Chromium **não** passa pelo tradutor de estado de tecla morta `ToUnicodeEx` do kernel Win32 quando despachado diretamente ao renderer).
     - Por isso, no método `CDP-DeadKeyThenComposed`, o evento `rawKeyDown` da tecla morta entrega `key: "Dead", code: "Quote"` para ouvintes de scan code/canvas (como o console VMware HTML5), e o `keyDown` seguinte entrega `code: "KeyA", text: "ã"`. Já no `CDP-ComposedTextDirect`, os eventos intermediários `Dead` são omitidos, o que funciona para `<textarea>` DOM comum mas **não** envia o scan code da tecla morta caso um console remoto dependa exclusivamente de `KeyboardEvent.code` físico.
3. **Autenticação Corporativa, Certificados e Complexidade Operacional (§2):**
   - O navegador integrado usa perfil isolado (`%LocalAppData%\VmTyper\WebView2Profile`), exigindo novo login pelo usuário e podendo sofrer restrições de políticas de Acesso Condicional que bloqueiam user-agents/hosts WebView2 embutidos.

## Decisão

1. **Manter o Modo Externo + `SendInput` como padrão recomendado do VmTyper:**
   - O `SendInput` (com `HighResolutionDelayProvider`, `InjectedKeyTracker` e `TargetGuard` das Fases 1–3) opera diretamente sobre o navegador corporativo já autenticado pelo usuário, atravessa o pipeline real de teclado do Windows (`WM_KEYDOWN` + tradução nativa de layout) e não introduz superfície de manutenção de navegador embutido.
2. **Manter `Features.EmbeddedBrowser = false` e `Features.CdpInput = false` por padrão:**
   - A PoC em `experiments/WebView2Poc/` e os adaptadores `CdpKeyEventMapper` / `CdpInputInjector` / `TargetGuard.CheckEmbeddedBeforeUnit` permanecem disponíveis sob feature flag experimental para cenários específicos de teste automatizado sem foco Win32 ou diagnóstico DOM, sem adicionar dependência obrigatória do WebView2 ao executável principal.

