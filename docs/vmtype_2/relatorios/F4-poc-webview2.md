# Relatório da Fase 4 — Navegador Integrado com WebView2 (Experimental)

- **Data:** 2026-11-09
- **Branch:** `feature/vmtyper2`
- **Escopo:** `docs/vmtype_2/fases/fase-4-webview2.md` (§1 a §5)

---

## 1. Documentação Oficial Consultada e Versões Fixadas (§1.1 e §3)

Antes da implementação da PoC, foram consultadas as especificações oficiais do `Microsoft.Web.WebView2` e do Chrome DevTools Protocol (`Input.dispatchKeyEvent`):

- **Pacote NuGet fixado em `Directory.Packages.props`:** `Microsoft.Web.WebView2` **`1.0.4314.35`**
- **Links oficiais consultados:**
  1. `CoreWebView2.SetVirtualHostNameToFolderMapping`: https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2.setvirtualhostnametofoldermapping
  2. `CoreWebView2.CallDevToolsProtocolMethodAsync`: https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2.calldevtoolsprotocolmethodasync
  3. `CoreWebView2Environment.GetAvailableBrowserVersionString`: https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2environment.getavailablebrowserversionstring
  4. `CoreWebView2EnvironmentOptions.AllowSingleSignOnUsingOSPrimaryAccount`: https://learn.microsoft.com/dotnet/api/microsoft.web.webview2.core.corewebview2environmentoptions.allowsinglesignonusingosprimaryaccount
  5. Chrome DevTools Protocol `Input.dispatchKeyEvent`: https://chromedevtools.github.io/devtools-protocol/tot/Input/#method-dispatchKeyEvent
  6. W3C UI Events `KeyboardEvent.code` Values: https://www.w3.org/TR/uievents-code/

---

## 2. Arquitetura da PoC Isolada e dos Adaptadores (§1, §3, §4, §5)

1. **PoC Isolada (`experiments/WebView2Poc/`):**
   - Projeto WPF separado (`net10.0-windows`) referenciando `Microsoft.Web.WebView2` (`1.0.4314.35`) e `VmTyper.csproj`.
   - Verifica `CoreWebView2Environment.GetAvailableBrowserVersionString()` antes de instanciar o controle; se ausente, exibe aviso claro sem instalar nada automaticamente.
   - Inicializa `CoreWebView2Environment.CreateAsync` com `userDataFolder` dedicado em `%LocalAppData%\VmTyper\WebView2Profile`.
   - Mapeia `tools/diagnostics-page/` como `https://diag.vmtyper.local/index.html` via `SetVirtualHostNameToFolderMapping`.
2. **Mapeador e Injetor CDP (`VmTyper/Services/CdpKeyEventMapper.cs`):**
   - `CdpKeyEventMapper` converte `InputUnit` do `KeyStrokePlanner` em chamadas `Input.dispatchKeyEvent` com `code` derivado da tabela de Scan Code Set 1 (incluindo `IntlRo` `0x73`, `IntlBackslash` `0x56`, `Semicolon` `0x27`, `Quote` `0x28`, `BracketLeft` `0x1A` e `NumpadDivide` `0xE035`), `windowsVirtualKeyCode`, `modifiers` (Alt=1, Ctrl=2, Meta=4, Shift=8), `location` e `text`/`unmodifiedText`.
   - Suporta e testa as duas estratégias de tecla morta exigidas no §3:
     - `CdpDeadKeyStrategy.DeadKeyThenComposed`: envia `rawKeyDown`/`keyUp` da tecla morta com `key: "Dead"` e `code` físico (ex.: `"Quote"`, `"BracketLeft"`), seguido do `keyDown`/`keyUp` da tecla base com o caractere composto em `text`.
     - `CdpDeadKeyStrategy.ComposedTextDirect`: envia apenas o par `keyDown`/`keyUp` da tecla base com o texto composto em `text`.
   - `CdpInputInjector` implementa `IInputInjector`, permitindo que `TypingScheduler`, `TypingTimingPolicy`, `TypingMetrics` e `TypingDiagnosticsEngine` sejam compartilhados integralmente entre `SendInput` e `CDP`.
3. **Controle de Foco Integrado (`TargetGuard.CheckEmbeddedBeforeUnit`, §4):**
   - Verifica antes de cada unidade: janela em foreground, foco do controle WebView2, foco do documento (`document.hasFocus()`), elemento ativo (`document.activeElement`) e integridade do processo (`IsWebViewProcessAlive`).
   - Níveis não verificáveis (como iframe de outra origem ou prontidão interna da VM convidada) são sempre reportados como `"desconhecido"`, nunca como `"ok"` (testado em `TargetGuard_EmbeddedMode_ReportsUnknownForUnverifiableLevels_AndPausesOnAnyLoss`).
4. **Janela Integrada Experimental (`VmTyper/Views/EmbeddedBrowserWindow.xaml`, §5):**
   - Mantém os controles em barra superior separada do container do WebView2 (sem sobreposição de airspace HWND).
   - Desligada por padrão (`Features.EmbeddedBrowser = false`, `Features.CdpInput = false`); o projeto principal `VmTyper.csproj` não carrega nem depende do `Microsoft.Web.WebView2` no modo externo.

---

## 3. Perfil, Autenticação, Certificados e Rede (§2)

| Tema | Abordagem Implementada | Limite / Comportamento Observado |
|---|---|---|
| Persistência de sessão e cookies | `userDataFolder` próprio em `%LocalAppData%\VmTyper\WebView2Profile` | Sessões são persistidas entre aberturas da PoC; cookies de navegadores externos não são copiados. |
| SSO / autenticação integrada | `CoreWebView2EnvironmentOptions.AllowSingleSignOnUsingOSPrimaryAccount` configurável via `--allow-sso` | Caso o provedor de identidade corporativo bloqueie user-agents embutidos, não há contorno; usa-se o modo externo. |
| Certificados de cliente | Handler `ClientCertificateRequested` mantém `args.Handled = false` | Exibe o diálogo nativo para escolha manual da pessoa, sem seleção automática silenciosa. |
| Certificados de servidor inválidos | Handler `ServerCertificateErrorDetected` força `CoreWebView2ServerCertificateErrorAction.Default` | Nunca ignora erros TLS automaticamente; registra no log e exibe erro amigável. |
| Credenciais | Nenhuma captura ou preenchimento de senha | Login sempre realizado manualmente pela pessoa. |

---

## 4. Comparação Experimental: `SendInput` vs `CDP Input.dispatchKeyEvent` (§1.7, §1.8 e §3)

O experimento comparativo foi executado em `experiments/WebView2Poc/` contra o Evergreen WebView2 Runtime **`157.0.4341.42`** (NuGet `Microsoft.Web.WebView2` `1.0.4314.35`) e a página HTML de diagnóstico mapeada em `https://diag.vmtyper.local/index.html` (`SetVirtualHostNameToFolderMapping`). Os dados brutos completos estão em `docs/vmtype_2/relatorios/F3-dados/f4-webview2-comparison.json`.

### 4.1 Resultado por Corpus e Método no WebView2 (`https://diag.vmtyper.local/index.html`)

| Corpus | Comprimento Esperado | SHA-256 (12 hex) | Método | `keyDown` / `keyUp` | `isTrusted` (`true`/`false`) | `code` Vazio | Eventos `Dead` | `repeat` / Double-Down | Divergências (`DivergenceClassifier`) |
|---|---:|---|---|---:|---:|---:|---:|---:|---:|
| `basico.txt` | 151 | `8c679c94de2c` | **B1: `CDP-DeadKeyThenComposed`** | 243 / 243 | 243 / 0 | 0 | 29 | 0 / 0 | **0** (`IsIdentical = true`) |
| `basico.txt` | 151 | `8c679c94de2c` | **B2: `CDP-ComposedTextDirect`** | 214 / 214 | 214 / 0 | 0 | 0 | 0 / 0 | **0** (`IsIdentical = true`) |
| `repetidas.txt` | 660 | `06f028d6ea15` | **B1: `CDP-DeadKeyThenComposed`** | 674 / 674 | 674 / 0 | 0 | 0 | 0 / 0 | **0** (`IsIdentical = true`) |
| `repetidas.txt` | 660 | `06f028d6ea15` | **B2: `CDP-ComposedTextDirect`** | 674 / 674 | 674 / 0 | 0 | 0 | 0 / 0 | **0** (`IsIdentical = true`) |
| `acentos-ptbr.txt` | 510 | `0738bd5295ad` | **B1: `CDP-DeadKeyThenComposed`** | 737 / 737 | 737 / 0 | 0 | 77 | 0 / 0 | **0** (`IsIdentical = true`) |
| `acentos-ptbr.txt` | 510 | `0738bd5295ad` | **B2: `CDP-ComposedTextDirect`** | 660 / 660 | 660 / 0 | 0 | 0 | 0 / 0 | **0** (`IsIdentical = true`) |
| `teclas-mortas-literais.txt` | 362 | `ed987d19fb0b` | **B1: `CDP-DeadKeyThenComposed`** | 477 / 477 | 477 / 0 | 0 | 44 | 0 / 0 | **0** (`IsIdentical = true`) |
| `teclas-mortas-literais.txt` | 362 | `ed987d19fb0b` | **B2: `CDP-ComposedTextDirect`** | 433 / 433 | 433 / 0 | 0 | 0 | 0 / 0 | **0** (`IsIdentical = true`) |
| `numeros-simbolos.txt` | 318 | `11dbed697bdd` | **B1: `CDP-DeadKeyThenComposed`** | 394 / 394 | 394 / 0 | 0 | 2 | 0 / 0 | **0** (`IsIdentical = true`) |
| `numeros-simbolos.txt` | 318 | `11dbed697bdd` | **B2: `CDP-ComposedTextDirect`** | 392 / 392 | 392 / 0 | 0 | 0 | 0 / 0 | **0** (`IsIdentical = true`) |
| `tabs-indentacao.txt` | 335 | `ce8a7a356103` | **B1: `CDP-DeadKeyThenComposed`** | 355 / 355 | 355 / 0 | 0 | 0 | 0 / 0 | **0** (`IsIdentical = true`) |
| `tabs-indentacao.txt` | 335 | `ce8a7a356103` | **B2: `CDP-ComposedTextDirect`** | 355 / 355 | 355 / 0 | 0 | 0 | 0 / 0 | **0** (`IsIdentical = true`) |
| Todos os 6 corpora | — | — | **A: `SendInput-Win32` (no receptor local F3 §3)** | 100% pares | N/A (`WM_KEYDOWN`) | 0 | N/A | 0 / 0 | **0** (`f3-matriz-local-corpora-metodos-perfis.json`) |
| Todos os 6 corpora | — | — | **A: `SendInput-Win32` (em sessão sem foreground OS)** | 0 / 0 | 0 / 0 | 0 | 0 | 0 / 0 | Bloqueado quando `GetForegroundWindow() == 0x0` (requer janela ativa no desktop interativo) |
| Sessão VMware real (§1.8) | — | — | **A (`SendInput`) vs B1 (`CDP Dead+Base`) vs B2 (`CDP Composed`)** | Pendente (VMware) | Pendente (VMware) | Pendente (VMware) | Pendente (VMware) | Pendente (VMware) | **Pendente (VMware)** — requer sessão autorizada com pessoa presente e receptor na VM convidada |

### 4.2 Principais Achados Técnicos e Recomendação (ADR 0009)

1. **`isTrusted` e `KeyboardEvent.code` no CDP:** Todos os eventos despachados via `Input.dispatchKeyEvent` no WebView2 (`157.0.4341.42`) chegam ao DOM com `isTrusted: true` e `code` físico preenchido (`KeyA`..`KeyZ`, `Quote`, `BracketLeft`, `Semicolon`, `IntlRo`, `IntlBackslash`, `Tab`, `Enter`, etc.), `EmptyCodeCount = 0`, `RepeatCount = 0` e `DoubleDownWithoutUpCount = 0`.
2. **Duas estratégias para teclas mortas ABNT2 (`~`, `^`, `` ` ``, `´`, `¨`):**
   - `CdpDeadKeyStrategy.DeadKeyThenComposed` (**B1**) preserva os eventos DOM intermediários `key: "Dead"` com `code` físico (ex.: `77` eventos `Dead` em `acentos-ptbr.txt`) e entrega `0` divergências no texto final (`SHA-256 = 0738bd5295ad`). É a estratégia mais fiel para consoles que inspecionam tanto `KeyboardEvent.code` quanto `text`.
   - `CdpDeadKeyStrategy.ComposedTextDirect` (**B2**) omite o par intermediário `Dead` e entrega o grafema composto diretamente na tecla base (`660` `keyDown` vs `737` em B1 no `acentos-ptbr.txt`), também com `0` divergências no `<textarea>`.
   - Para teclas mortas literais seguidas de espaço (`teclas-mortas-literais.txt`, ex.: `~a`, `^^`, `~~`), a combinação `VK_SPACE` do `KeyStrokePlanner` + resolução de layout via `Win32.GetKeyboardLayout(0)` quando `GetForegroundWindow() == 0x0` garantiu `0` divergências em B1 e B2 (`362/362` caracteres, `SHA-256 = ed987d19fb0b`).
3. **Independência de foco OS vs. exigência de foreground:** Enquanto `SendInput` depende estritamente de a janela alvo estar em foreground no desktop interativo (`GetForegroundWindow()`), `CDP Input.dispatchKeyEvent` entrega os eventos diretamente ao `RenderWidgetHost` do WebView2. Ainda assim, no produto (`EmbeddedBrowserWindow`), `TargetGuard.CheckEmbeddedBeforeUnit` exige foco explícito e reporta níveis não verificáveis (iframe cross-origin e prontidão da VM convidada) como `"desconhecido"`.
4. **Decisão arquitetural (`docs/vmtype_2/adrs/0009-avaliacao-webview2-e-cdp-vs-sendinput.md`):**
   - O **modo externo** permanece como padrão e caminho primário do VmTyper.
   - `Features.EmbeddedBrowser` e `Features.CdpInput` permanecem `false` por padrão até a execução da sessão comparativa em ambiente VMware real (`Pendente (VMware)`).

---

## 5. Critérios de Aceite da Fase 4

- [x] PoC funcional com a página de diagnóstico carregada via `SetVirtualHostNameToFolderMapping` (`https://diag.vmtyper.local/index.html`) e os dois métodos (`SendInput` e `CDP Input.dispatchKeyEvent` nas variantes `DeadKeyThenComposed` e `ComposedTextDirect`) testados (`experiments/WebView2Poc/`).
- [x] Tabela comparativa `SendInput` vs `CDP` com dados reais em `docs/vmtype_2/relatorios/F3-dados/f4-webview2-comparison.json` e seção `Pendente (VMware)` claramente demarcada para a sessão em ambiente corporativo.
- [x] Modo externo intacto e sem dependência obrigatória do WebView2 (`VmTyper.csproj` não referencia `Microsoft.Web.WebView2`; 105/105 testes passando).
- [x] Todos os limites de segurança da §2 respeitados (sem cópia de cookies externos, sem armazenamento de credenciais, sem seleção silenciosa de certificado de cliente e sem ignorar erros de certificado TLS).
- [x] Nenhum nível de foco não verificável reportado como "ok" (`TargetGuard.CheckEmbeddedBeforeUnit` reporta `"desconhecido"`, coberto por teste unitário em `tests/VmTyper.Tests/CdpAndEmbeddedFocusTests.cs`).


