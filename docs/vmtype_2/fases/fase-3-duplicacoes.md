# Fase 3: Investigação e prevenção de caracteres duplicados

**Objetivo:** descobrir, com evidência, **onde** no pipeline `VmTyper → Windows → navegador → console VMware → rede → guest` os caracteres são duplicados (ou trocados, ou omitidos), e aplicar prevenção só onde a evidência sustentar.

**Pré-requisitos:** F1 (correções N1/H/E, que eliminam causas no próprio app) e F2 (temporização precisa e reprodutível). A **F3a** (§1 e §2) pode começar em paralelo com a F2.

---

## §0 Regra de evidência
Cada hipótese termina em um destes estados: **Sustentada** (reprodução + mecanismo coerente), **Refutada** (condição testada sem efeito), **Não testável no ambiente** ou **Pendente (VMware)**. O relatório nunca diz "causa raiz" sem reprodução controlada.

---

## §1 `TypingDiagnosticsEngine` (F3a)
- Consome `UnitResult`/`KeyTiming` do scheduler e grava **JSONL** em `%AppData%\VmTyper\diagnostics\session-<sessionId>.jsonl`.
- Registro por evento:
  ```json
  {"s":"<sessionId>","i":123,"cat":"letter","m":"Scan","vk":65,"sc":30,"ext":false,
   "mods":["LShift"],"tDown":123456789,"tUp":123476789,"ret":[1,1],"err":0,
   "gapNext":41.2,"hwnd":"0x1234","fg":"chrome","note":null}
  ```
  `tDown`/`tUp` vêm de `Stopwatch.GetTimestamp()` (convertido para µs no relatório).
- **Sem conteúdo por padrão.** O campo `ch` só aparece com `DetailedTrace` ativo (F1 §4.3).
- Cabeçalho da sessão: versão do app, perfil, método, layout, flags, resolução de timer, SO e `SPI_GETKEYBOARDDELAY`/`SPEED` do host.
- Rotação: manter as últimas N sessões (configurável) e oferecer um botão "Excluir diagnósticos".

## §2 Página HTML de diagnóstico (nível navegador, F3a)
`tools/diagnostics-page/index.html`, estática, servida localmente (por exemplo, `dotnet serve` ou `python -m http.server`) ou aberta via `file://`:
- `textarea` + listener de `keydown`/`keyup`/`beforeinput`/`input` que registra `key`, `code`, `location`, `repeat`, `isTrusted`, `timeStamp` (`performance.now()`) e modificadores.
- Exporta JSON (botão e download) e expõe o resumo em `window.__vmtyperDiag` para automação.
- Detecção imediata na página: `keydown` com `repeat = true`, ou dois `keydown` do mesmo `code` sem `keyup` entre eles → marcado como "suspeito".
- **Limitações a documentar:** não enxerga dentro de iframes de outra origem, não enxerga o console VMware (canvas + WebSocket) e não substitui o receptor na VM.

## §3 Receptor na VM (nível VMware, exige pessoa)
- O mesmo `VmTyper.TestReceiver` da F0, publicado como **um único exe portátil** (self-contained), executado **dentro da VM de teste autorizada**.
- Como não há área de transferência nem, necessariamente, transferência de arquivo:
  1. O receptor exibe **na tela**: comprimento, SHA-256 (primeiros 12 hex) e contagem por categoria do texto recebido.
  2. O VmTyper exibe os mesmos valores do texto **esperado**.
  3. Se divergirem e a política permitir exportar arquivos da VM, o receptor exporta JSON (texto + eventos `WH_KEYBOARD_LL`) para análise completa. Caso contrário, a pessoa anota as primeiras divergências exibidas pelo receptor (ele mostra "posição da 1ª diferença" quando recebe o texto esperado por *digitação* de um arquivo de corpus que já existe dentro da VM, ou seja, o corpus é copiado para a VM uma única vez pelo meio autorizado).
- O hook no guest também registra **auto-repeat no guest** (KeyDown repetido sem KeyUp), que é a evidência direta da hipótese de jitter de rede.

## §4 Corpora (`tests/corpora/`)
| Arquivo | Conteúdo |
|---|---|
| `repetidas.txt` | `aa`, `ll`, `ss`, `oo`, `mm`, `ee` em palavras reais (`carro`, `pessoa`, `alla`) e sequências de 10/50/200 idênticos |
| `acentos-ptbr.txt` | todas as vogais com ´ ` ^ ~ ¨, ç/Ç, maiúsculas acentuadas |
| `teclas-mortas-literais.txt` | `~`, `^`, `´`, `` ` ``, `¨`, `'`, `"` isolados, seguidos de espaço, de vogal, de si mesmos |
| `numeros-simbolos.txt` | `R$ 1.250,00`, `!@#$%&*()-_=+[]{};:/?\|<>` |
| `codigo-delphi.pas` / `codigo-csharp.cs` | indentação com espaços e tabs, linhas vazias, strings com aspas |
| `longo-10k.txt` | prosa com cerca de 10 mil caracteres |
| `tabs-indentacao.txt` | combinações de tabs, espaços e linhas vazias consecutivas |

Cada corpus tem um `.sha256` versionado.

## §5 Classificador de divergências
`DivergenceClassifier` (puro, testável):
1. Alinhamento esperado × recebido por **diff de Myers em nível de grapheme** (edit script).
2. Classificação de cada operação:
   - **Duplicação:** inserção igual ao grapheme imediatamente anterior ou posterior.
   - **Omissão:** remoção.
   - **Substituição:** troca 1↔1. Subtipo **falha de acentuação** quando base(esperado) = base(recebido) e o diacrítico difere, ou quando aparece o acento isolado + letra.
   - **Ordem incorreta:** par adjacente trocado.
   - **Divergência de indentação:** diferença só em espaço/tab no início de linha.
3. Saída: contagens por classe, posições e contexto (±5 unidades, **mascarado** quando o DetailedTrace estiver desligado: só categorias).
4. Correlação com o JSONL: para cada divergência, o evento enviado no índice correspondente (gap, hold, retorno, foco).

## §6 Matriz de hipóteses × experimentos
| # | Hipótese | Experimento | Nível |
|---|---|---|---|
| H1 | KeyUp perdido | hook no receptor: Down sem Up; comparar com o JSONL (Up enviado com retorno 1?) | local, VM |
| H2 | Auto-repeat no guest por latência | hook no guest: Down repetidos; correlacionar com gap/hold e com latência de rede induzida (se permitido) | VM |
| H3 | Duplicação no navegador | página HTML: `repeat`/Down duplo com SendInput | navegador |
| H4 | Peculiaridade do console VMware | diferença VM − navegador, com o mesmo corpus e perfil | VM |
| H5 | Repetição no SO da VM | `repeat delay` do guest no mínimo × máximo | VM |
| H6 | Mudança de foco | incidentes do TargetGuard × posições das divergências | todos |
| H7 | Eventos duplicados pelo app | JSONL: o mesmo índice enviado duas vezes? (a F1 deve tornar isso impossível) | local |
| H8 | Modificadores presos | hook: estado de Shift/Ctrl no momento de cada Down | local, VM |
| H9 | Falha em tecla morta | corpus de teclas mortas literais, antes e depois da correção N1 | local, VM |
| H10 | Temporização insuficiente | varrer gap 0/3/10/30/60 e hold 2/15/60/120 | todos |
| H11 | Interferência de substituições | mesmo corpus com e sem regras | local |
| H12 | Físico × virtual × Unicode | matriz de métodos | todos |
| H13 | Layout host ≠ VM | VM em layout diferente do host | VM |
| H14 | Repetição de tecla idêntica | `repetidas.txt` × `SameKeyMinGapMs` 0/20/50 | todos |

**Execução:** ferramenta de linha de comando `tools/VmTyper.Bench` (ou modo `--bench` no app) que lê uma matriz YAML `{corpus × método × perfil × repetições}`, digita no receptor (local ou navegador, de forma automática) e grava resultados em `relatorios/F3-dados/*.json`. Para a VM: o roteiro é o mesmo, mas a pessoa confirma o foco e registra o hash e as divergências.

**Repetições:** no mínimo 5 por célula no nível local/navegador. Na VM, o número é definido com a pessoa conforme o tempo disponível, e o relatório informa o N real. Os resultados são apresentados como contagens e taxas por 10 mil unidades, **com N**, sem extrapolar.

## §7 Prevenção adaptativa (só depois dos dados)
Políticas candidatas. Cada uma só é ativada se uma hipótese for **Sustentada** e o experimento mostrar efeito:
- Piso de gap entre unidades idênticas (H1/H14).
- Gap extra depois de Enter ou linhas de código (H10).
- Teto de hold (H2/H5).
- **Desaceleração adaptativa:** se a latência da própria chamada `SendInput` ou o atraso obtido ultrapassarem um limiar (sinal de sistema sobrecarregado), aumentar o gap temporariamente.
- Pausa ao detectar instabilidade de foco recorrente.
- Recomendação automática de perfil e método por ambiente, salva como "perfil do ambiente X".

**Proibido:**
- remover caracteres repetidos;
- Backspace automático;
- reenvio por suspeita.

Sem observação, o resultado é **Desconhecido**.

## Entregáveis
1. `relatorios/F3-duplicacoes.md`, com uma tabela por nível (Local / Navegador / VMware), por método e perfil, com N, contagens por classe e o status de cada hipótese.
2. Dados brutos em `relatorios/F3-dados/` (sem conteúdo sensível, porque os corpora são sintéticos).
3. ADR com o perfil e o método recomendados por ambiente, ou "sem evidência suficiente".

## Critérios de aceite da F3
- [ ] Engine de diagnóstico, página HTML, receptor e classificador com testes.
- [ ] Matriz executada nos níveis local e navegador, com dados publicados.
- [ ] Nível VMware executado **ou** marcado "Pendente (VMware)", com o roteiro pronto.
- [ ] Toda política de prevenção ativada aponta para a hipótese e os dados que a justificam.
