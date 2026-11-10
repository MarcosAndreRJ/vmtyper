# ADR 0008: Recomendação de perfil e método de injeção por ambiente

- **Data:** 2026-11-09
- **Status:** Aceito (Fase 3)
- **Contexto:** A Fase 3 (§6–§7) exige registrar em ADR o perfil de temporização e o método de envio (`Automatico`, `TecladoFisico`, `Unicode`) recomendados por ambiente (Local, Navegador, Console VMware), ou declarar "sem evidência suficiente" quando a observação controlada não estiver concluída no respectivo nível.

## Evidências coletadas na Fase 3

1. **Nível Local (plano + `InputUnitExecutor` + `FakeKeyboardLayout.Abnt2()` + `DivergenceClassifier`, `N = 5` por célula em `docs/vmtype_2/relatorios/F3-dados/f3-matriz-local-corpora-metodos-perfis.json`):**
   - Com o método **`Automatico`** (scan codes para teclas mapeadas no layout + tecla morta + `VK_SPACE` para literais `~ ^ ´ ` ¨ ' "` + fallback Unicode apenas quando necessário), todos os 8 corpora da Fase 3 (`repetidas.txt`, `acentos-ptbr.txt`, `teclas-mortas-literais.txt`, `numeros-simbolos.txt`, `codigo-delphi.pas`, `codigo-csharp.cs`, `tabs-indentacao.txt`, `longo-10k.txt`) apresentaram **0 divergências** (`divergenceRatePer10k = 0.0`) nos 4 perfis (`Legado`, `Rápido`, `Compatibilidade VM`, `Natural`).
   - Com o método **`TecladoFisico`**, caracteres fora do mapa direto do layout (ou que dependem de fallback Unicode quando não mapeados) são omitidos e registrados como não suportados, elevando a taxa de omissões em corpora que contêm símbolos fora do layout alvo.
   - Com o método **`Unicode`** (`KEYEVENTF_UNICODE`), a entrega funciona em controles Win32/WPF locais e no DOM do navegador local, mas consoles remotos baseados em scan code (como o VMware Web Console / VMRC) podem ignorar pacotes `KEYEVENTF_UNICODE` sem scan code físico.
2. **Prevenção de colisão de teclas idênticas (`repetidas.txt`, H14) e sobrecarga do host (H10):**
   - Na varredura `f3-varredura-h10-h14-repetidas.json`, o piso `SameKeyMinGapMs` (`20 ms` no perfil `Rápido`, `32 ms` no `Natural`, `50 ms` no `Compatibilidade VM`) garante separação mínima entre `KeyUp(i)` e `KeyDown(i+1)` da mesma tecla física mesmo quando o gap base sorteado é menor.
   - A desaceleração adaptativa (`EnableAdaptiveSlowdown = true` em `TypingOptions`) adiciona `+15 ms` no gap subsequente apenas quando a chamada `SendInput`/hold excede `Hold + 8 ms` (sinal medido de preempção/sobrecarga do host), sem alterar o conteúdo do plano.
3. **Nível Console VMware (`Pendente (VMware)`):**
   - Como não há sessão VMware conectada ao ambiente de execução automatizada, as hipóteses exclusivas de rede/guest VMware (`H2`, `H4`, `H5`, `H13`) permanecem com status **Pendente (VMware)**.

## Decisão

1. **Método padrão recomendado em todos os ambientes:** **`Automatico`**.
   - Preserva scan codes reais e sequências de tecla morta para o console VMware (corrigidas na Fase 1 para literais seguidos de `VK_SPACE`), mantendo fallback Unicode apenas para símbolos inexistentes no layout alvo.
2. **Perfil padrão do aplicativo:** manter **`Legado`** (`AdaptiveTiming = false` por padrão) até a execução do roteiro presencial na VM com o receptor portátil (`VmTyper.TestReceiver --hook`).
3. **Perfil recomendado para sessões VMware com latência variável ou repetição de caracteres:** **`Compatibilidade VM`** (`HoldMs = [12, 22]`, `GapMs = [35, 55]`, `SameKeyMinGapMs = 50 ms`, `EnterExtraGapMs = 80 ms`) com `EnableAdaptiveSlowdown = true`, pois maximiza a margem contra fusão de pacotes `KeyUp/KeyDown` e contra auto-repeat no guest sem ultrapassar o teto seguro de hold (`30 ms`).

## Consequências
- Nenhuma política proibida (remoção de caracteres repetidos, Backspace automático ou reenvio por suspeita) foi implementada.
- Quando os dados presenciais na VM forem coletados com `VmTyper.TestReceiver`, este ADR poderá ser atualizado para promover `Compatibilidade VM` ou `Natural` a padrão caso a evidência em VMware sustente a mudança.
