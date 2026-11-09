# ADR 0001: Um evento por chamada `SendInput` + `InjectedKeyTracker`

- **Status:** Aceito
- **Data:** 2026-11-09
- **Fase:** F1 (§2.2)

## Contexto
No motor v1 (`KeyboardInputService`), uma combinação de teclas (ex.: `Shift+A`) enviava todas as descidas (`downs`) em um único array para `SendInput` e todas as subidas (`ups`) em outro array. Isso trouxe três problemas confirmados por teste na Fase 0:
1. **Defeito H1:** se a chamada das subidas falhasse ou a das descidas fosse parcial (`sent < array.Length`), a exceção subia para o `finally` do bloco, que chamava `ReleaseAllModifiers` — soltando apenas modificadores e deixando a tecla principal (`A`) presa (`PartialSendInput_ReleasesMainKey`).
2. **Defeito H2:** as descidas iam por scan code (`KEYEVENTF_SCANCODE`), mas `ReleaseAllModifiers` enviava `KeyUp` por virtual key (`wVk`), não casando o par em receptores que rastreiam por scan code (`ReleaseUsesSameEncodingAsPress`).
3. **Defeito H3:** o `finally` rodava também em caso de sucesso, enviando 6 `KeyUp` espúrios por bloco sem `KeyDown` correspondente (`SuccessfulBlock_SendsNoOrphanKeyUps`).

## Decisão
1. Enviar cada evento (`KeyDown` ou `KeyUp`) em uma chamada `SendInput` individual de 1 elemento (`nInputs = 1`).
2. Manter um `InjectedKeyTracker` que registra cada `KeyDown` confirmado (`retorno == 1`) com sua codificação exata (`Scan` ou `Unicode`, código e flag `Extended`) e remove cada `KeyUp` confirmado.
3. No `finally` de cada unidade (`KeyStrokeExecutor.ExecuteUnit`), executar `_tracker.ReleaseAll()` — que solta em ordem inversa **somente** as teclas presentes no tracker, com a **mesma codificação** usada na descida, tentando até 3 vezes (com 5 ms entre tentativas) e registrando `StuckKeyRisk` se falhar.
4. Remover o `ReleaseAllModifiers` incondicional do fim do bloco; mantê-lo apenas como último recurso em `App.OnDispatcherUnhandledException`, também baseado no `InjectedKeyTracker`.

## Alternativas consideradas
| Alternativa | Por que não |
|---|---|
| Manter `SendInput` em lote e inspecionar o retorno `sent` para deduzir quais índices do array entraram | Embora a documentação do Win32 indique inserção sequencial, tratar falhas parciais no meio de lotes de subida/descida é propenso a erros de índice; com 1 evento por chamada, o resultado é binário (`0` ou `1`). |
| Soltar todos os modificadores conhecidos no `finally` por scan code | Continuaria gerando `KeyUp` órfãos em blocos bem-sucedidos (H3) e não liberaria a tecla principal (H1). |

## Consequências
- **Positivas:** zero `KeyUp` órfão em blocos normais; zero tecla presa (modificador ou principal) em falhas parciais simuladas em qualquer posição; par `KeyDown`/`KeyUp` sempre na mesma codificação.
- **Negativas:** uma combinação `Shift+letra` faz 4 chamadas `SendInput` em vez de 2 (custo de ~microssegundos por tecla, desprezível frente à folga de 2 ms de hold).
- **Como reverter:** reverter o commit da F1 §2 restaura `Win32.SendInputs` em lote.
