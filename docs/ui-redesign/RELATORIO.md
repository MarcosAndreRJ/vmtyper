# VmTyper 2.0 — Redesign de UI/UX e Dark Mode: relatório

Branch `feature/smart-editor-delphi-sql`. Nada foi commitado. WPF + AvalonEdit mantidos; o motor de digitação não foi alterado.

## 1. Auditoria: problemas encontrados

| # | Problema | Causa |
|---|----------|-------|
| 1 | **"Documento vazio" / botão Digitar desabilitado após digitar** | O editor grava direto em `SourceDocument.Text`; nada re-dividia em blocos nem notificava os comandos. Os blocos ficavam desatualizados — e digitar um bloco usava intervalos de um texto antigo. |
| 2 | Hierarquia visual plana | Barra superior com 4 combos + abas de documento + barra de divisão + abas de blocos + botões de rodapé com larguras fixas, tudo no mesmo peso. |
| 3 | Redundâncias | "Diagnóstico" duplicado (topo e rodapé); estatísticas duplicadas (painel e rodapé); mesma ação em 3 botões ("Digitar bloco / documento / seleção") sem dizer o que cada um envia. |
| 4 | Cores fixas em hexadecimal (≈40 pontos) | Impossível trocar de tema; badges de entrega, cartões da fila, Configurações e Diagnóstico com cores claras embutidas. |
| 5 | Fontes de 10–11,5 px e botões com largura fixa | Cortes e sobreposição na barra de execução em janelas estreitas. |
| 6 | Estado desabilitado mudo | Botões desabilitados sem explicar o motivo. |
| 7 | Salvar arquivo inexistente | Era possível abrir `.pas/.sql`, mas não salvar; sem indicador de alterações. |

## 2. O que mudou

**Layout em 5 regiões** (`MainWindow.xaml`): barra superior compacta → abas de documentos (nome, `●` não salvo, linguagem, fechar, menu de overflow) → barra de ferramentas do documento → **navegador de Estrutura** (substitui a 2ª fileira de abas de blocos) + editor dominante → painel contextual recolhível/redimensionável (Status / Histórico / Log) → barra de execução adaptativa.

**Ação primária contextual**: o botão diz o que será digitado — `Digitar documento`, `Digitar bloco: <título>`, `Digitar N caracteres selecionados`, `Digitar bloco N` (fila) — com subtítulo e atalho. O menu ▾ escolhe documento/bloco/seleção. Desabilitada, a barra explica o motivo (ex.: "O documento está vazio. Digite, cole ou abra um arquivo para habilitar."). **Pausar** e **Cancelar** ficam sempre visíveis. O atalho global usa o mesmo critério do rótulo (`ExecutionTarget`), então nunca digita algo diferente do exibido.

**Sistema de design** (`Themes/`): `LightTheme.xaml`/`DarkTheme.xaml` (paletas desenhadas separadamente, pincéis semânticos + sobrescrita de `SystemColors`), `Tokens.xaml` (tipografia, espaçamento, raios), `DesignSystem.xaml` + `Controls.xaml` (Button/Primary/Danger/Toolbar, TextBox, ComboBox completo, CheckBox, RadioButton, TabControl/TabItem, ScrollBar, ProgressBar, ToolTip, ListBox, Menu/ContextMenu, Expander, GroupBox, DataGrid, divisores). Ícones: Segoe MDL2 Assets.

**Tema Escuro / Claro / Automático**: troca em tempo de execução sem recriar a janela (`ThemeManager`), persistida em `AppSettings.Theme`, Auto segue o Windows. Aplicado ao editor (fundo, texto, números de linha, linha atual, seleção, cursor, painel de busca, sintaxe Delphi e SQL — `*.Dark.xshd`), Configurações, Diagnóstico e Navegador Integrado. Badges de estado se adaptam ao tema (`ThemedHexConverter`, `BadgeColors`).

**Correção do "Documento vazio"**: `DocumentManager` observa o texto do documento atual, re-divide com debounce de 350 ms (`FlushPendingSplit()` aplica na hora) preservando o bloco selecionado e reavaliando a linguagem; o VM força a divisão antes de qualquer digitação.

**Documentos**: `Salvar` / `Salvar como` preservando a codificação (UTF-8, UTF-8 BOM, UTF-16, Windows-1252; se o texto não couber em ANSI grava UTF-8 com BOM em vez de perder dados), indicador `●`, confirmação ao fechar com alterações.

**Responsividade**: janela mínima 880×560; abaixo de 1180 DIP entra o modo compacto (rótulos secundários ocultos, ícones + tooltips; painéis laterais nascem mais estreitos). Largura inicial limitada à área útil do monitor.

**Atalhos**: `Ctrl+O/N/S/Shift+S/W/F/G` (modo Editor), `Ctrl+Shift+E` estrutura, `Ctrl+Shift+L` painel de status. O painel **não** usa `Ctrl+Shift+P` porque é o atalho global de pausa.

## 3. Decisões

- Estilo implícito de `TextBlock` **removido** de propósito: ele sobrescrevia `Foreground`/`FontSize` herdados e quebrava estados (aba selecionada, desabilitado). Cor e fonte vêm de `AppWindowStyle`.
- `TabControl`/`TabItem` definem `Foreground` explicitamente: o estilo padrão do tema fixa preto e cortava a herança vinda da janela (texto ilegível no escuro). A cor do cabeçalho é aplicada só no `ContentPresenter` (`TextElement.*`) para não vazar para o conteúdo da aba.
- Painel de busca do AvalonEdit: em vez de reescrever o template da biblioteca, as chaves de `SystemColors` são redirecionadas por tema.
- Contadores de entrega: **nenhum contador novo**. Mantidos Enviado/Confirmado/Suspeito/Desconhecido/Falha como já eram calculados; "Confirmado" nunca é derivado localmente.
- Testes de UI usam **uma thread de UI compartilhada** (`UiTestHost`): o `Application` do WPF é singleton e seus recursos pertencem à thread que o criou.

## 4. Arquivos

Novos: `Themes/{LightTheme,DarkTheme,Tokens,DesignSystem,Controls}.xaml`, `Resources/Syntax/{Delphi,FirebirdSql}.Dark.xshd`, `Services/ThemeManager.cs`, `Models/AppTheme.cs`, `Helpers/{ThemeConverter,ThemedHexConverter,BadgeColors,BoolToVisibilityConverter}.cs`, `tests/.../{UiTestHost,UiRedesignTests}.cs`, `docs/ui-redesign/*`.

Alterados: `App.xaml(.cs)`, `MainWindow.xaml(.cs)`, `Editor/CodeEditorControl.xaml(.cs)`, `Editor/SyntaxHighlightingManager.cs`, `Documents/DocumentManager.cs`, `Models/{AppSettings,SourceDocument,CodeBlock}.cs`, `ViewModels/{MainViewModel,SettingsViewModel}.cs`, `Services/SettingsMapper.cs`, `Views/{Settings,Diagnostics,EmbeddedBrowser}Window.xaml(.cs)`, `tests/.../MainWindowDiagnosticTests.cs`.

## 5. Testes e resultados

- `dotnet test tests/VmTyper.Tests` (x64): **212 aprovados, 0 falhas** (56 em `UiRedesignTests` + 5 portados em `MainWindowDiagnosticTests`).
- `tests/VmTyper.IntegrationTests`: 8 testes, **todos ignorados por desenho** (exigem VM/navegador reais) — não foram executados.
- Build Release x64: **0 avisos, 0 erros**.
- Cobertura nova: troca de tema em runtime (recursos, editor, janela), ciclo Auto→Escuro→Claro→Auto + persistência, round-trip em `SettingsService`, sintaxe distinta por tema (Delphi/SQL), conversores, documento vazio → ação desabilitada + motivo, re-divisão com debounce e `FlushPendingSplit`, troca de documentos, alvo da ação primária (documento/bloco/seleção/fila), Pausar/Cancelar sempre visíveis com nome de acessibilidade/tooltip, recolher/restaurar painéis, navegação pela estrutura, modo compacto por largura, tabela de atalhos, salvar com codificação, **layout sem sobreposição/corte em 6 tamanhos × 2 temas** (880×560, 1024×728, 1093×614 [1366×768@125%], 1366×728, 1280×680 [1920×1080@150%], 1920×1040), renderização real com verificação de cor do tema por pixel, regressão da herança de cor em abas.

## 6. Validação visual — o que foi e o que NÃO foi feito

Feito: capturas por `RenderTargetBitmap` das **janelas reais** (`MainWindow`, `SettingsWindow`, `DiagnosticsWindow`, mesma classe e XAML do app), claro e escuro, inspecionadas uma a uma. Isso encontrou e corrigiu: texto azul/menor dentro das abas (herança do `TabItem`), texto preto ilegível no escuro em Configurações/Diagnóstico (`Foreground` do tema padrão), painel de busca branco no escuro, caixas de texto cortadas, abas de Configurações em duas fileiras, atalho em conflito com a pausa global, espaço vazio do contador.

**Não feito / limitações:**
- **O executável real não foi iniciado por mim** (a execução foi bloqueada pelo classificador de permissões). O caminho `App.xaml` → `ThemeManager` → configurações só foi validado por compilação e por testes que reproduzem a mesma ordem de dicionários. Para fechar: `docs/ui-redesign/capture-real-window.ps1 -ExePath <exe> -OutPng <png>`, ou simplesmente abrir o app.
- As capturas **não são capturas de tela do SO**: popups (dropdown de combo, menus de contexto, tooltips), barra de título do Windows, `MessageBox` e diálogos de arquivo são nativos/separados e não aparecem nem seguem o tema.
- DPI: o layout foi validado nas **dimensões em DIP** equivalentes a 125%/150%; não testei renderização em monitores reais com escala.
- `EmbeddedBrowserWindow`: cores trocadas por pincéis do tema, mas **não renderizada** (depende de WebView2).
- Painel de busca (Ctrl+F): o contêiner segue o tema; os ícones dos botões apareceram em branco nas capturas (claro e escuro) — não sei se é artefato da captura.
- Aba "Atalhos" das Configurações (seletor de tema): não aparece nas capturas (só a aba Digitação).
- Modo Automático ao vivo (mudar o tema do Windows com o app aberto): caminho de código existe (`UserPreferenceChanged`), mas não foi exercitado.

## 7. Defeito pré-existente observado (não alterado)

`LanguageDetectionService` usa `Confidence >= Medium` nos ramos `.inc`/`.txt`, mas `DetectionConfidence` é ordenado de `High=0` a `Uncertain=3` — a comparação está invertida. Corrigi os dois usos equivalentes em `DocumentManager` (o que fazia a linguagem nunca ser detectada ao colar código); os do serviço ficaram como estavam.

## 8. Entregáveis

- Executável (Release x64): `VmTyper\bin\x64\Release\net10.0-windows\VmTyper.exe`
- Capturas (24): `docs/ui-redesign/screenshots/` — `editor-{light|dark}-{880x560…1920x1040}`, `fila-*`, `configuracoes-*`, `diagnostico-*`, `estado-documento-vazio-*`, `estado-digitando-*`, `editor-busca-aberta-*`. Regerar: `VMTYPER_UI_SCREENSHOTS=<pasta> dotnet test tests/VmTyper.Tests -p:Platform=x64 --filter UiRedesignTests`.
