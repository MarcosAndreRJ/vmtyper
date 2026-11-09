# ADR 0003: TargetGuard ligado por padrão no modo HWND

- **Status:** Aceito
- **Data:** 2026-11-09
- **Fase:** F1 (§3.3)

## Contexto
Na implementação v1 (`MainViewModel` + `ActiveWindowService`), a verificação da janela de destino só ocorria uma única vez após a contagem regressiva e apenas quando `ValidateTargetWindow` estava habilitado (cujo padrão em `AppSettings` era `false`). Durante o envio do bloco, se o usuário trocasse de janela (`Alt+Tab`, clique acidental, pop-up ou notificação do sistema), o `VmTyper` continuava injetando teclas em qualquer janela que assumisse o primeiro plano (defeito C, severidade Alta). Além disso, `ActiveWindowService` chamava `Process.GetProcessById` a cada consulta sem cache (defeito N13).

## Decisão
1. Introduzir o `TargetGuard` e deixá-lo **ligado por padrão** (`AppSettings.EnableTargetGuard = true`) no modo HWND:
   - Ao final da contagem regressiva, captura a `TargetAnchor` contendo o `HWND` de topo (`GetAncestor(GA_ROOT)`), `PID`, `ProcessName` e `Title`. Rejeita o início se o primeiro plano ainda for o próprio `VmTyper` ou, quando `ValidateTargetWindow` estiver ligado, se o processo não corresponder ao navegador configurado.
   - Durante a digitação, monitora o evento `EVENT_SYSTEM_FOREGROUND` (`SetWinEventHook` com `WINEVENT_OUTOFCONTEXT`) e executa uma verificação síncrona barata (`GetForegroundWindow` + `GetAncestor(GA_ROOT)`) antes de cada unidade lógica, reutilizando `PID` e `ProcessName` em cache na âncora (N13).
2. Quando o destino é perdido, a unidade em curso termina normalmente (sem tecla presa) e a sessão entra imediatamente em `PausadoDestinoPerdido` (`Pausado: destino perdido`), registrando um `TargetLostIncident`.
3. Ao retomar, executa uma contagem curta (padrão 2 s) e revalida a âncora; se o `HWND` mudou, exige confirmação explícita do usuário ("O destino mudou. Usar a nova janela como destino?").

## Alternativas consideradas
| Alternativa | Por que não |
|---|---|
| Manter desligado por padrão como em `ValidateTargetWindow` | Deixaria o usuário desprotegido contra perda de foco no meio de blocos longos a menos que descobrisse a opção manualmente. O modo HWND não exige configurar qual navegador está em uso, então pode ficar ligado por padrão sem atrito. |
| Cancelar a sessão imediatamente ao perder o foco | Descartaria o progresso do bloco por causa de um pop-up ou troca acidental de janela; pausar permite voltar à janela original e retomar do índice exato. |

## Consequências
- **Positivas:** nenhuma unidade é enviada para uma segunda janela que roube o foco durante a digitação; zero chamadas a `Process.GetProcessById` no laço por unidade; retomada segura com revalidação de HWND.
- **Negativas / limitações:** o guard detecta troca de janela de topo no host, mas não detecta troca de aba sem mudança de título (a menos que `TargetGuardStrict` esteja ativo e o título mude) nem mudança de foco de campo dentro da VM.
- **Como reverter:** o usuário pode desmarcar `EnableTargetGuard` na aba Segurança das Configurações.
