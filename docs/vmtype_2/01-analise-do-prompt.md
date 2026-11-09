# Análise do prompt mestre: VmTyper 2.0

## 1. Resumo do que o prompt pede

Sete fases que transformam o VmTyper em um sistema confiável, adaptativo e observável:

| Fase do prompt | Tema |
|---|---|
| 0 | Auditoria, problemas A–I e linha de base |
| 1 | Estabilização: fila, motor de teclado, TargetGuard, persistência |
| 2 | Motor de temporização humana e adaptativa (perfis) |
| 3 | Investigação e prevenção de caracteres duplicados |
| 4 | Navegador integrado WebView2 (SendInput vs CDP) |
| 5 | Interface |
| 6 | Testes, migração para .NET 10, organização do repositório |
| 7 | Relatório por fase |

A diretriz central é **fidelidade do texto acima de velocidade e de funcionalidades**. O prompt também tem uma política forte contra falsas afirmações: não inventar números, não declarar testes que não rodaram e distinguir "enviado" de "confirmado".

## 2. Pontos fortes

1. **Prioridade explícita e verificável.** "Fidelidade > velocidade > naturalidade" serve como critério de desempate em todas as decisões.
2. **Honestidade epistêmica.** As regras de não declarar causa raiz sem evidência, não tratar o retorno de `SendInput` como confirmação e classificar o resultado como "desconhecido" quando não há observação são exatamente o que o projeto precisa. O README atual viola essa regra (ver N6 na avaliação).
3. **Proíbe "correções" destrutivas.** Backspace automático, remoção de caracteres repetidos e erros humanos simulados ficam vetados de forma explícita.
4. **Preserva o legado.** Modo antigo como opção e feature flags para o que é experimental.
5. **Os problemas A–I são reais.** Oito dos nove foram confirmados no código. Ver [02-avaliacao-da-implementacao.md](02-avaliacao-da-implementacao.md).
6. **Exige separar testes locais de testes reais na VMware.**

## 3. Lacunas e riscos do prompt

### 3.1 Falta definir o "ground truth" (lacuna principal)
A Fase 3 depende de "comparar texto esperado e recebido somente quando houver um método confiável", mas o prompt **não define esse método**. Esse é o ponto que mais trava o projeto:
- O console VMware HTML5 renderiza a tela em canvas. Não dá para ler o texto da VM a partir do navegador.
- Sem um receptor instrumentado, não há como medir duplicação. As Fases 2.2 ("calibrar por experimentação") e 3.5 ("prevenção com base em evidência") ficam sem base.

**Ajuste recomendado:** criar um **harness de verdade-terreno** em três níveis, detalhado na [Fase 3](fases/fase-3-duplicacoes.md):
1. Receptor local no host (app de teste com hook de teclado e caixa de texto), totalmente automatizável.
2. Página HTML de diagnóstico no navegador (registra `keydown`/`keyup`/`repeat`/`code`), automatizável.
3. O mesmo receptor rodando **dentro da VM**, com hash e contagem exibidos na tela para comparação humana. É o único nível que mede o pipeline VMware real e exige uma pessoa.

Esse harness deve ser construído **já na Fase 0/1** (níveis 1 e 2), porque é ele que prova as correções da Fase 1.

### 3.2 Tensão entre "digitação humana" e fidelidade
A Fase 2 é chamada de "prioritária", mas o prompt não diz **por que** um ritmo humano melhoraria a fidelidade. As hipóteses legítimas são:
- consoles que perdem eventos em rajadas regulares;
- buffers que transbordam;
- ritmo mais lento depois de pontuação ou Enter, quando o editor da VM faz mais trabalho (autoindentação, autocomplete).

Por outro lado, um ritmo "natural" aumenta o tempo total e, com ele, a janela de exposição a perda de foco e jitter de rede.

**Ajuste recomendado:**
- Tratar a Fase 2 como **"motor de temporização configurável"**. A "naturalidade" é só um dos perfis.
- O perfil-padrão continua **Legado** até que a Fase 3 meça que outro perfil é melhor.
- Corrigir antes o problema real de temporização: a resolução do timer (N5), sem a qual nenhum perfil é reproduzível.
- Registrar no ADR que o objetivo do perfil Natural é **compatibilidade de ritmo**, não evitar detecção. Contornar mecanismos anti-automação de terceiros fica fora de escopo, em linha com a regra de não contornar políticas.

### 3.3 Hipóteses de duplicação incompletas
A lista 3.1 do prompt é boa, mas deixa de fora causas que a auditoria encontrou no código e que são **mais baratas de verificar**:
- **Tecla morta literal** (N1): `~`, `^`, `´`, `` ` ``, `¨` combinam com o caractere seguinte ou engolem o espaço.
- **Layout do host ≠ layout da VM** (N2).
- **Modificadores físicos do usuário** ainda pressionados depois do atalho `Ctrl+Shift+Enter` (H4).
- **6 KeyUp espúrios por bloco** e liberação por VK em vez de scan code (H2 e H3).
- **Auto-repeat no guest causado por jitter de rede.** O KeyUp chega ao guest atrasado mais do que o *repeat delay* do próprio guest. Nesse caso, segurar a tecla por mais tempo no host não ajuda, e o fator relevante é a latência. Isso muda a interpretação de 2.4 ("tempos maiores não são sempre mais seguros").

### 3.4 Escopo e ordem
- **O volume de trabalho é grande para uma execução contínua.** O "INICIE AGORA / não parar no plano" do prompt pressupõe um agente com acesso a VMware, credenciais e muitas horas de trabalho. As Fases 3 (VMware real) e 4 (SSO corporativo) dependem de uma pessoa. O plano marca esses pontos explicitamente ([03-plano-de-fases.md](03-plano-de-fases.md), seção 4).
- **A "Fase 7" não é uma fase.** É um entregável recorrente. Ela vira o [template de relatório](templates/relatorio-de-fase.md) obrigatório ao fim de cada fase.
- **Os testes não podem ficar para a Fase 6.** O projeto de testes e as abstrações de injeção precisam existir já na Fase 0, senão as Fases 1–3 não têm como cumprir o critério "coberto por testes".
- **A higiene do repositório não pode ficar para a Fase 6.** Com `bin/` e `obj/` versionados, cada build suja 29 arquivos rastreados. Ela sobe para a Fase 0.
- **A migração para .NET 10 é mais urgente do que "avaliar".** O .NET 8 perde suporte em 10/11/2026, e a máquina só tem o SDK 10. O prompt pede que seja uma etapa isolada, e isso está correto. Recomenda-se executá-la **logo depois da Fase 1** (Fase 1B), com a suíte de regressão da Fase 1 servindo de rede de segurança, e não no fim.

### 3.5 Ambiguidades a resolver (decisões propostas)

| Ambiguidade | Decisão proposta |
|---|---|
| Estados "Pendente" vs "Pronto" | Pendente = vazio ou sem conteúdo elegível. Pronto = tem texto e pode ser digitado. "Próximo" deixa de ser status e vira um ponteiro (`NextBlockId`). |
| O que fazer com um bloco parcialmente enviado | Novo estado `PartiallySent`, com `SentUpToIndex`. Para retomar, o usuário escolhe "do início" ou "do índice N", sempre com confirmação, porque o estado do destino é desconhecido. |
| TargetGuard quando o destino não é confirmável | Pausa automática (não cancela) e retomada com revalidação e uma contagem curta. |
| Persistir texto sensível | Opção com três valores: `Completo` (padrão atual), `Criptografado` (DPAPI, usuário atual) e `Nunca` (só metadados). |
| WebView2 no mesmo executável ou separado | PoC em `experiments/` primeiro. A integração vai para o app principal atrás de uma flag desligada por padrão. |
| O CDP conta como "digitação sem área de transferência"? | Sim: são eventos de teclado sintéticos injetados no renderer, não colagem. Registrar em um ADR. |

### 3.6 Riscos técnicos não citados
- **Airspace no WPF:** o WebView2 é hospedado em HWND, então controles WPF não podem se sobrepor a ele. Isso afeta o design da Fase 4.
- **WebView2 Runtime ausente** em máquinas corporativas exige detecção e fallback.
- **Eventos CDP chegam como `isTrusted = true`** no documento, mas o console pode depender de `code` e `keyCode` corretos, o que exige mapeamento de layout próprio.
- **Hooks de baixo nível (WH_KEYBOARD_LL)** no receptor de teste podem ser bloqueados por EDR corporativo. Use apenas na máquina de teste.
- **`SetWinEventHook`** para detectar troca de foco exige um laço de mensagens, que a UI thread já tem.

## 4. Veredito

O prompt é **tecnicamente sólido e bem intencionado**, com regras de honestidade excelentes. Ele é executável desde que três ajustes sejam feitos:
1. **Definir o ground truth** (harness em três níveis) antes de prometer métricas de duplicação.
2. **Reordenar:** higiene do repositório, testes e abstrações na Fase 0, e migração para .NET 10 como Fase 1B.
3. **Rebaixar a "digitação humana" de prioridade para opção calibrável.** O ganho real de fidelidade vem das correções de mapeamento, liberação de teclas e foco da Fase 1, além da temporização precisa da Fase 2.

Nota sobre o modo de execução: o prompt termina com "INICIE AGORA". Este documento atende ao pedido de **analisar, avaliar e planejar**. A implementação começa pela Fase 0 quando for autorizada.
