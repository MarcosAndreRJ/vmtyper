# Guia de Distribuição e Publicação do VmTyper

Este documento detalha os procedimentos de publicação, os requisitos de runtime e a estrutura de dados em tempo de execução do VmTyper.

---

## 1. Requisitos de Sistema

- **Sistema Operacional:** Windows 10 (versão 1803 ou superior recomendada para suporte a timers de alta resolução `CreateWaitableTimerExW`) ou Windows 11.
- **Arquitetura:** x64 (o layout das estruturas de interoperabilidade Win32 e chamadas P/Invoke é dimensionado para 64 bits).
- **Runtime do .NET:** .NET 10.0 Desktop Runtime (x64) para publicações dependentes de framework.
- **WebView2 Runtime (Opcional):** Necessário exclusivamente se a funcionalidade experimental de navegador integrado (`EmbeddedBrowser`) for ativada pelo usuário.

---

## 2. Comandos de Publicação

### 2.1 Publicação Dependente de Framework (Padrão)
Gera binários enxutos que utilizam o .NET 10 Desktop Runtime instalado no sistema operacional do usuário:

```powershell
dotnet publish VmTyper\VmTyper.csproj -c Release -r win-x64 --no-self-contained -o dist\framework-dependent
```

### 2.2 Publicação Autocontida (Self-Contained / Arquivo Único)
Conforme avaliado no [ADR 0006](adr/0006-avaliacao-publicacao-self-contained-single-file.md), a versão autocontida embute o runtime do .NET 10 diretamente na pasta de distribuição, permitindo execução em máquinas sem pré-instalação do runtime:

```powershell
dotnet publish VmTyper\VmTyper.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o dist\self-contained
```

---

## 3. Diretórios de Dados do Usuário

O VmTyper segue as diretrizes da Microsoft para armazenamento de configurações e dados em tempo de execução:

### 3.1 Dados de Configuração e Fila (`%AppData%\VmTyper\`)
- `settings.json`: preferências de temporização, métodos de digitação, regras de substituição de caracteres e mapeamento de atalhos.
- `queue.json`: estado persistido da fila de blocos de texto, ponteiro atual e status de cada item.
- `history.json`: registro histórico resumido das sessões concluídas (metadados e contagens, sem armazenar senhas ou textos confidenciais).
- `logs\`: logs técnicos rotacionados por data (`vmtyper-YYYY-MM-DD.log`).

### 3.2 Perfil de Navegador Embutido (`%LocalAppData%\VmTyper\`)
- `WebView2Profile\`: cache e dados de sessão do runtime do WebView2 quando o modo de navegador integrado estiver ativo.

