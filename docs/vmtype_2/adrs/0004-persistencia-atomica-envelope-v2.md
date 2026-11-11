# ADR 0004: Envelope JSON v2 + gravação atômica + preservação de arquivo corrompido

- **Status:** Aceito
- **Data:** 2026-11-09
- **Fase:** F1 (§4.1)

## Contexto
Na implementação v1 (`QueuePersistenceService`, `SettingsService`, `HistoryService`), qualquer erro de leitura ou desserialização retornava uma lista vazia ou um objeto padrão sem avisar o usuário. Como a inicialização da fila criava um bloco vazio e o autosave gravava com `File.WriteAllText`, um `queue.json` ou `settings.json` corrompido ou truncado era sobrescrito na primeira mutação (defeito F, severidade Crítica, confirmado por `CorruptQueueFile_IsNotOverwritten` e `CorruptSettingsFile_IsReported`). Além disso, `File.WriteAllText` direto podia truncar o arquivo principal se o processo terminasse no meio da escrita.

## Decisão
1. Introduzir `AtomicJsonStore<T>` para `queue.json`, `settings.json` e `history.json`:
   - **Save:** serializa com envelope `{ "schemaVersion": 2, "savedAt": "...", "data": ... }` → grava em `<arq>.tmp` → `FileStream.Flush(true)` → `File.Replace(<arq>.tmp, <arq>, <arq>.bak)` (ou `File.Move` se `<arq>` ainda não existir).
   - **Load:** tenta `<arq>` e valida (IDs únicos, `Order` contíguo, `SentUpToIndex <= Text.Length`, enums válidos); se falhar, tenta `<arq>.bak` e preserva o principal corrompido; se ambos falharem, renomeia `<arq>` para `<arq>.corrupt-<yyyyMMddHHmmss>` e retorna `LoadStatus.Corrupt`.
2. Migrar automaticamente na leitura o formato v1 (array ou objeto sem envelope) para o schema v2.
3. Oferecer `AppSettings.QueueTextPersistence` (`Full | Encrypted | None`), usando DPAPI (`ProtectedData.Protect(..., DataProtectionScope.CurrentUser)`) no modo `Encrypted`.

## Alternativas consideradas
| Alternativa | Por que não |
|---|---|
| Usar banco SQLite local | Adiciona dependência externa pesada e perde a facilidade de inspeção/suporte manual dos arquivos JSON. |
| Sobrescrever com padrões após exibir aviso simples | Se o usuário fechar o diálogo ou o autosave disparar antes de uma cópia manual, o arquivo corrompido ainda seria perdido. Renomear para `.corrupt-*` preserva os dados por construção. |

## Consequências
- **Positivas:** uma escrita interrompida afeta apenas `.tmp`; um arquivo corrompido nunca é sobrescrito; o backup `.bak` recupera automaticamente a última versão íntegra; falhas de escrita disparam `PersistenceFailed` visível na barra de status.
- **Negativas:** cada arquivo persistido passa a ter um `.bak` complementar no diretório `%AppData%\VmTyper`.
- **Como reverter:** o campo `data` do envelope v2 preserva a estrutura da coleção/objeto, facilitando leitura por ferramentas externas.
