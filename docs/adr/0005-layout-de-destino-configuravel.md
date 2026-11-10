# ADR 0005: Layout de destino configurável

- **Status:** Aceito
- **Data:** 2026-11-09
- **Fase:** F1 (§2.1)

## Contexto
No motor v1 (`KeyboardMappingService`), a conversão de caractere para scan code consultava sempre `GetKeyboardLayout` da janela do navegador no host (`HostForeground`). Quando a VM convidada utiliza um layout diferente do host (por exemplo, host em US-International e VM em ABNT2, ou vice-versa), os scan codes gerados produzem caracteres diferentes na VM (defeito N2).

## Decisão
1. Adicionar `AppSettings.TargetLayout` com o padrão `"HostForeground"` ou um KLID explícito (por exemplo, `"00010416"` para Português Brasil ABNT2, `"00020409"` para Inglês US-International, `"00000409"` para US).
2. Resolver o HKL via `LoadKeyboardLayout(klid, KLF_NOTELLSHELL)` apenas para mapeamento (`VkKeyScanEx` e `MapVirtualKeyEx`), sem alterar o layout ativo do shell do usuário no host.
3. Exibir na interface o layout configurado com o aviso de que ele deve coincidir com o layout configurado dentro da VM.

## Alternativas consideradas
| Alternativa | Por que não |
|---|---|
| Tabelas estáticas hardcoded por idioma no código C# | Duplicaria as tabelas oficiais do Windows e ficaria incompleta para símbolos menos comuns. |
| Detectar automaticamente o layout interno da VM | Impossível sem agente instalado dentro da VM, pois o host enxerga apenas a janela do navegador. |

## Consequências
- **Positivas:** permite digitar em modo Teclado Físico para uma VM cujo layout difere do host, sem que o usuário precise trocar o layout do próprio Windows.
- **Negativas:** exige que o usuário saiba qual layout está configurado dentro da VM caso não coincida com o do host.
- **Como reverter:** manter `TargetLayout = "HostForeground"` (que continua sendo o valor padrão).
