# ADR 0002: "Próximo" como ponteiro (`NextBlockId`), não como status

- **Status:** Aceito
- **Data:** 2026-11-09
- **Fase:** F1 (§1.1)

## Contexto
No modelo v1, o fato de um bloco ser o próximo a ser digitado era representado como um valor do enum `TextBlockStatus` (`Current`), misturando o estado do bloco com o ponteiro de navegação da fila. Isso causou dois defeitos confirmados por teste na Fase 0:
- **Defeito A:** editar ou esvaziar o texto de um bloco não recalculava os status (`EditingEmptyBlock_MakesItNext`, `ClearingCurrentBlock_MovesNextToFollowing`).
- **Defeito B:** `SetAsNext(id)` rebaixava apenas blocos a partir de `Order >= alvo`, e `RecalculateStatuses()` atribuía `Current` ao primeiro bloco não concluído da fila — ignorando o alvo quando havia um bloco elegível antes dele (`SetAsNext_WithEarlierReadyBlock_SelectsTarget`).

Além disso, um bloco cancelado no meio ou interrompido por fechamento do aplicativo precisava manter seu estado real (`PartiallySent`, `Interrupted`, `Failed`) ao mesmo tempo em que continuava apontado como próximo.

## Decisão
1. Remover `Current` e `Pending` de `TextBlockStatus` e adotar estados ortogonais ao ponteiro: `Empty`, `Ready`, `Typing`, `Paused`, `Sent`, `PartiallySent`, `Cancelled`, `Failed`, `Interrupted`.
2. Representar "próximo" como `TypingQueueService.NextBlockId` (refletido na propriedade calculada `TextBlockItem.IsNext`), derivado de `ManualNextId` (se apontar para um bloco elegível em `Ready`/`PartiallySent`/`Interrupted`/`Failed`) ou, na ausência dele, do primeiro bloco `Ready` na ordem.
3. Centralizar todas as mudanças de estado no método único `TypingQueueService.Transition(id, evento)`.

## Alternativas consideradas
| Alternativa | Por que não |
|---|---|
| Manter `Current` no enum e ajustar `SetAsNext` para rebaixar blocos anteriores | Perderia a distinção entre um bloco `PartiallySent`/`Interrupted`/`Failed` que também é o próximo e exigiria mutar o status de blocos não relacionados. |
| Usar apenas um índice inteiro (`NextIndex`) | Quebraria ao reordenar ou remover blocos; o `Guid` (`NextBlockId` / `ManualNextId`) acompanha a identidade do bloco. |

## Consequências
- **Positivas:** `SetAsNext` grava apenas `ManualNextId`, sem tocar no status de outros blocos; `PartiallySent`, `Interrupted` e `Failed` preservam seu diagnóstico enquanto continuam sendo o próximo a digitar; apenas `AllSent` avança o ponteiro para o bloco seguinte.
- **Negativas / migração:** exige migrar arquivos `queue.json` v1 (`Pending` → `Ready`/`Empty`, `Current` → `Ready`, `Completed` → `Sent`, `Typing` → `Interrupted`).
- **Como reverter:** reverter o commit da F1 §1 restaura o enum v1, mas reintroduz os defeitos A e B.
