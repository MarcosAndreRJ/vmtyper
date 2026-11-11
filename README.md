# VmTyper

Aplicativo desktop Windows (C# / .NET 10 LTS / WPF) projetado para digitar textos longos, com suporte completo a caracteres acentuados do português brasileiro (ABNT2 e US-International), dentro de máquinas virtuais VMware acessadas via console HTML5 no navegador ou VMRC — **sem utilizar a área de transferência do sistema operacional**.

Cada caractere é decomposto em eventos reais de teclado físico via `SendInput` (com opção secundária por injeção direta de code points Unicode ou CDP via WebView2) e despachado ao processo em primeiro plano no host.

---

## 1. Principais Recursos

- **Fila de Blocos de Texto:** gerencie múltiplos blocos de texto preparados previamente. Cada acionamento de envio (botão ou atalho global) transmite exatamente o bloco apontado e avança deterministicamente.
- **Perfis de Digitação Configuráveis:**
  - *Legado:* compatibilidade estrita com comportamento de versões anteriores.
  - *Rápido:* menor latência e intervalos curtos para editores locais.
  - *Adaptativo:* variação temporal com distribuição contínua para evitar rejeição em consoles virtualizados.
  - *Conservador:* maior sustentação de tecla (*Hold*) e espaçamento seguro para conexões remotas com latência ou instabilidade.
- **Proteção de Janela Ativa (`TargetGuard`):** monitoramento contínuo em nível de sistema (`WinEventHook`) que detecta perda de foco da janela-alvo e pausa a digitação imediatamente para evitar envio acidental a outros programas.
- **Persistência Atômica:** armazenamento em envelope versionado (v2) com escrita atômica (`.tmp` + substituição segura), eliminando risco de corrupção de arquivos em encerramentos abruptos.
- **Semântica Rigorosa de Entrega:** status explícito em tela distinguindo eventos **enviados** de eventos **confirmados**, com contagem transparente de unidades enviadas, puladas e falhas.
- **Substituição Flexível de Caracteres:** regras personalizadas de substituição (modo texto ou combinação física exata de teclas) processadas em passe único sem encadeamentos espúrios.
- **Suporte Experimental a Navegador Embutido:** WebView2 integrado com opção de injeção direta via Chrome DevTools Protocol (`Input.dispatchKeyEvent`).

---

## 2. Histórico de Mudanças

O desenvolvimento do VmTyper evoluiu através de etapas estruturadas de estabilização e refatoração arquitetural:

- **Estabilização do Pipeline de Injeção:** separação rigorosa de `KeyDown` e `KeyUp` com tempos de sustentação (*Hold*) determinísticos, eliminação de reentrância por meio de `TypingSessionCoordinator` centralizado e liberação de emergência de teclas presas.
- **Normalização e Teclas Mortas:** decomposição NFD e mapeamento de sequências mortas tanto para o layout ABNT2 quanto para layouts internacionais (ex.: US-International).
- **Preservação de Indentação:** envio de `Shift+Home` seguido de sobreposição ao digitar em editores de código (como Delphi/Lazarus) para neutralizar a autoindentação acumulativa sem apagar conteúdo adjacente.
- **Supervisão de Destino:** introdução do `TargetGuard` ancorando o `HWND` raiz e o `PID` do navegador host para prevenir digitação fora da janela pretendida.
- **Migração de Plataforma:** atualização para **.NET 10 LTS** com suporte ao SDK 10.0.401, compilação estrita com `-warnaserror` e suíte de testes unitários automatizados.
- **Investigação Experimental de Duplicações:** caracterização empírica de eventos em consoles web, adição de telemetria e testes de ritmo com sementes pseudoaleatórias controladas.

---

## 3. Decisões Técnicas e Arquitetura

Para detalhes aprofundados sobre os contratos de serviço, diagramas de sequência e garantias de concorrência, consulte:
- [Documento de Arquitetura](docs/ARCHITECTURE.md)
- [Registro de Decisões de Arquitetura (ADRs)](docs/adr/)
- [Guia de Distribuição e Publicação](docs/DISTRIBUTION.md)

### Resumo das Diretrizes de Engenharia:
1. **Sem uso de Clipboard:** todo o texto é decomposto em passos de tecla individuais.
2. **Isolamento de P/Invoke:** chamadas nativas concentradas na camada de infraestrutura (`Native/Win32.cs` e `Services/`).
3. **Descarte Seguro de Recursos:** hotkeys globais e hooks de eventos do Windows são liberados deterministicamente ao término da sessão ou no descarte do objeto.
4. **Resolução de Temporização:** temporizadores de alta resolução baseados em `CreateWaitableTimerExW` e espera híbrida (*spin-wait* com `Stopwatch`) em submilisegundos.

---

## 4. Compilação e Execução

### Requisitos:
- .NET 10 SDK (versão `10.0.401` ou superior, fixada no `global.json`).
- Windows 10 (versão 1803+) ou Windows 11 (x64).

### Linha de Comando:
```powershell
# Restaurar dependências
dotnet restore

# Compilar solução em Release com avisos como erros
dotnet build VmTyper.sln -c Release -warnaserror

# Executar suíte de testes unitários
dotnet test VmTyper.sln -c Release --no-build --filter "Category=Unit"
```

O executável gerado localiza-se em:
`VmTyper\bin\x64\Release\net10.0-windows\VmTyper.exe`

---

## 5. Testes e Validação

A estratégia de testes inclui validações unitárias, integração com receptor e checklist para consoles VMware:
- Consulte [Guia de Testes](docs/TESTING.md) para instruções sobre execução das categorias `Unit`, `Integration`, `E2E-Browser` e `E2E-VMware`.
- Utilitário receptor de eventos disponível em `tools/VmTyper.TestReceiver`.
- Página HTML de diagnóstico web disponível em `tools/diagnostics-page/index.html`.

---

## 6. Limitações Conhecidas

- **UIPI (User Interface Privilege Isolation):** se o navegador ou processo-alvo estiver em execução com privilégios elevados (administrador) e o VmTyper estiver em execução com usuário padrão, o Windows bloqueia a injeção nativa de teclado. A solução requer executar o VmTyper também como administrador.
- **Escopo do TargetGuard:** a proteção de foco monitora o `HWND` raiz da janela no sistema operacional hospedeiro. Ela detecta mudanças de aplicativo no Windows, mas **não** detecta trocas de aba internas no navegador nem mudança de foco entre campos internos do sistema convidado na VM.
- **Ambientes de Alta Latência:** transmissões através de conexões de rede instáveis ou consoles com aceleração gráfica desativada podem exigir a seleção do perfil de temporização **Conservador** para evitar omissão de eventos pelo navegador host.
