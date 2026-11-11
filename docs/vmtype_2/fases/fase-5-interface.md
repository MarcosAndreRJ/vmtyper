# Fase 5: Interface e experiência de uso

**Objetivo:** expor com clareza o que as Fases 1–4 criaram, mudando a UI só onde for preciso. Redesenho puramente estético fica fora do escopo.

**Pré-requisitos:** F1–F3 (F4 opcional, já que os controles do navegador integrado só aparecem com a flag ligada).

---

## §1 Painel principal (`MainWindow.xaml`)
Layout em três faixas, mantendo a estrutura atual (fila à esquerda, histórico/log à direita):

1. **Barra de sessão (topo):**
   - indicador do **destino** (ícone + processo + título truncado + estado do guard: Monitorando / Perdido / Desligado);
   - **método** (Físico/Unicode/Automático) e **perfil** (ComboBoxes compactos);
   - **layout de destino**;
   - **ambiente** (Externo/Integrado, visível só com a flag).
2. **Fila:**
   - cada bloco mostra o estado com ícone **e** texto (Vazio, Pronto, Próximo ▶, Digitando, Pausado, Enviado, Parcial (N/M), Falha, Interrompido) e uma cor de apoio. A cor nunca é a única pista;
   - os botões de edição seguem `CanEditQueue`.
3. **Barra de execução (rodapé):**
   - DIGITAR BLOCO N / PAUSAR-CONTINUAR / CANCELAR;
   - progresso em **unidades** com `Enviadas · Puladas · Falhas`, tempo e CPS;
   - link "Diagnóstico".

Mensagens de conclusão:
- "Bloco 3: 1.240 unidades **enviadas**. Recebimento não verificado." Nunca "concluído com sucesso" sem receptor.
- Quando houver `Skipped > 0` ou `Failed > 0`, o aviso é em amarelo ou vermelho e o bloco fica `PartiallySent`.

## §2 Seleção de ambiente
- Externo (padrão).
- Integrado → abre a `EmbeddedBrowserWindow` → submétodo SendInput | CDP (experimental, com o rótulo "experimental").
- A troca de ambiente fica bloqueada durante a sessão.

## §3 Configurações avançadas (`SettingsWindow.xaml`, em abas)
| Aba | Conteúdo |
|---|---|
| Digitação | método, layout de destino, perfil + editor do Personalizado (campos de §2 da F2, com validação de limites e prévia "dry-run" da duração), preservar indentação |
| Segurança de foco | TargetGuard ligado/desligado, modo estrito (título), navegador esperado, contagem de retomada |
| Substituições | como hoje, com aviso de regra duplicada e validação de combinação |
| Atalhos | como hoje |
| Persistência | `QueueTextPersistence` (Completo/Criptografado/Nunca), local dos arquivos, botão "Abrir pasta" |
| Diagnóstico | registrar JSONL (on/off), `DetailedTrace` (com aviso de conteúdo sensível), retenção, "Excluir diagnósticos" |
| Navegador integrado | flag, pasta de perfil, "Limpar dados do perfil" (com confirmação) |
| Experimental | lista de feature flags com descrição |

- Botão **Restaurar padrões** por aba e global, sempre com confirmação.
- Migrar a cópia manual do code-behind para um `SettingsViewModel` com binding e validação (`INotifyDataErrorInfo`). Isso fecha o problema I com testes do mapeamento.

## §4 Painel de diagnóstico (`Views/DiagnosticsWindow.xaml`, ampliado)
- **Aba "Sessão atual / última":** linha do tempo de eventos (índice, categoria, método, gap e hold solicitados × obtidos, retorno), incidentes de foco e unidades puladas.
- **Aba "Teste de teclado":** o teste atual, agora pelo coordenador de sessão.
- **Aba "Comparação":** importar o JSON do receptor ou da página → resultado do classificador (F3 §5) com contagens por classe.
- **Aba "Desempenho por método/perfil":** tabela dos benchmarks já executados (lidos de `relatorios/F3-dados`).

**Semântica visual obrigatória** (ícone + texto + cor, legenda fixa):

| Estado | Quando |
|---|---|
| **Enviado** | `SendInput`/CDP retornou sucesso. Nada se sabe do destino |
| **Confirmado** | O receptor ou a página observou a unidade exatamente como esperado |
| **Suspeito** | Há indício (repeat, Down duplo, foco instável), sem confirmação |
| **Desconhecido** | Não existe observação disponível |
| **Falha** | Erro nativo, unidade pulada ou divergência confirmada |

Nenhum elemento da UI mostra "Confirmado" com base apenas no retorno de `SendInput`. Existe um teste de ViewModel para isso.

## §5 Acessibilidade e consistência
- `AutomationProperties.Name` em todos os botões. Ele também é usado pelos testes de UI Automation.
- Atalhos de teclado exibidos nos rótulos.
- Textos em pt-BR centralizados em `Resources/Strings.resx`, para revisão.

## Testes
- ViewModels: estados → rótulos/ícones, `CanEditQueue`, "Confirmado" nunca derivado de `Sent`.
- `SettingsViewModel`: validação, ida e volta, restaurar padrões.
- Smoke de UI Automation (local): abrir o app, criar 2 blocos, digitar no receptor, verificar estados e progresso.

## Critérios de aceite da F5
- [ ] Todas as funções novas acessíveis pela UI, com as experimentais atrás de flags.
- [ ] Os cinco estados de entrega visual e semanticamente distintos.
- [ ] Restaurar padrões funcionando.
- [ ] Nenhuma regressão na suíte.
