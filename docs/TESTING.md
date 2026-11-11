# Guia de Testes do VmTyper

Este documento explica como executar a suíte de testes do VmTyper, a taxonomia de categorias, o uso dos utilitários de teste e o protocolo de validação manual no console VMware.

---

## 1. Categorias de Teste

A suíte de testes é dividida em 4 categorias explícitas via atributos do xUnit (`[Trait("Category", ...)]`):

| Categoria | Trait | Requisitos | Onde roda | Descrição |
|---|---|---|---|---|
| **Unit** | `Category=Unit` | Nenhum (apenas SDK .NET) | Qualquer máquina, CI (GitHub Actions) | Testes rápidos de mapeamento de teclado, temporização determinística, regras de fila, persistência e ViewModels com fakes. |
| **Integration** | `Category=Integration` | Desktop interativo desbloqueado, `VMTYPER_INTEGRATION=1` | Estação de desenvolvimento | Injeta teclado real via `SendInput` no processo receptor local (`VmTyper.TestReceiver`) e valida fidelidade caractere por caractere. |
| **E2E-Browser** | `Category=E2E-Browser` | Desktop interativo, WebView2 Runtime, `VMTYPER_E2E_BROWSER=1` | Estação de desenvolvimento | Valida injeção no navegador embutido e processamento de eventos na página HTML de diagnóstico. |
| **E2E-VMware** | `Category=E2E-VMware` | Operador humano, console VMware ativo | Ambiente de teste com VM | Roteiro manual guiado para validar o comportamento do console sob latência e virtualização. |

---

## 2. Como Executar os Testes

### 2.1 Testes Unitários (Rápidos e CI)
Executa todos os 116 testes unitários em milissegundos sem interferir no teclado ou no mouse:

```powershell
dotnet test VmTyper.sln -c Release --no-build --filter "Category=Unit"
```

Com relatório TRX e cobertura de código:
```powershell
dotnet test VmTyper.sln -c Release --filter "Category=Unit" --logger trx --collect:"XPlat Code Coverage"
```

### 2.2 Testes de Integração com Receptor Local
> **Aviso:** os testes de integração simulam digitação real no desktop. Deixe o computador desbloqueado e **não utilize o teclado ou mouse** durante a execução.

```powershell
$env:VMTYPER_INTEGRATION = "1"
dotnet test tests/VmTyper.IntegrationTests -c Release --filter "Category=Integration"
$env:VMTYPER_INTEGRATION = ""
```

---

## 3. Utilitários de Teste

### 3.1 Receptor Local (`tools/VmTyper.TestReceiver`)
O receptor é um aplicativo WPF minimalista com uma caixa de texto que escuta e analisa eventos brutos de teclado para validar a fidelidade do texto recebido:
- Compilação: `dotnet build tools/VmTyper.TestReceiver`
- Execução: `tools\VmTyper.TestReceiver\bin\x64\Release\net10.0-windows\VmTyper.TestReceiver.exe`
- O receptor mede o texto recebido, conta caracteres e verifica se ocorreram desvios em relação ao corpus esperado.

### 3.2 Página de Diagnóstico HTML (`tools/diagnostics-page/index.html`)
Página web para diagnóstico de injeção em navegadores (externos ou integrados):
- Abra o arquivo `tools/diagnostics-page/index.html` em qualquer navegador (Chrome, Edge ou Firefox).
- Possui campos para capturar `keydown`, `keyup` e `input`, além de calcular a duração de pressão das teclas (*Hold time*) e intervalos inter-teclas (*Interval*).
- Permite verificar visualmente se o navegador host está agrupando ou perdendo eventos de tecla.

---

## 4. Protocolo de Teste Manual VMware (`E2E-VMware`)

Quando novos ambientes VMware ou consoles HTML5 forem atualizados, execute o seguinte protocolo manual:

### Pré-requisitos
1. Console VMware (vSphere Web Client HTML5 ou VMware Remote Console - VMRC) aberto em janela focada.
2. Sistema operacional convidado (Windows ou Linux) com editor de texto simples aberto e com foco (ex.: Bloco de Notas ou `gedit`).
3. VmTyper em execução no host.

### Passos de Execução
1. **Configuração de Perfil:**
   - No VmTyper, selecione o método **Automático** ou **Teclado Físico**.
   - Defina o perfil de digitação como **Adaptativo** (ou **Conservador** caso a conexão esteja com alta latência).
   - Habilite **TargetGuard** configurando o navegador alvo em **Configurações → Segurança**.
2. **Carregamento do Bloco:**
   - Carregue um bloco com texto representativo contendo acentuação, maiúsculas/minúsculas e pontuação (ex.: `ação, café, início, avó, órgão, coração; R$ 1.250,00; !@#$%&*()-_=+`).
3. **Disparo e Ancoragem:**
   - Clique em **DIGITAR** (ou atalho `Ctrl+Shift+Enter`).
   - Durante os 3 segundos de contagem regressiva, clique dentro da área de texto da VM para estabelecer o foco.
   - Verifique que o TargetGuard identificou e ancorou a janela do navegador.
4. **Verificação de Integridade Durante a Digitação:**
   - Observe a cadência e fluidez dos caracteres.
   - Pressione **ESC** no meio da digitação: verifique se o cancelamento foi instantâneo e se nenhuma tecla (como Shift ou Alt) ficou presa.
   - Retome a digitação e teste uma perda voluntária de foco (clicando fora do navegador): confirme se o VmTyper entra imediatamente em pausa e não envia caracteres para a janela indevida.
5. **Auditoria Pós-Execução:**
   - Compare o texto digitado na VM com o texto original do VmTyper.
   - Verifique se houve caracteres duplicados ou omitidos.
   - Caso haja falhas, registre o log técnico gerado na pasta de logs (`%AppData%\VmTyper\logs\`).

---

## 5. Protocolo de Calibração Manual no Console VMware HTML5 (Barra e Acentuação)

### 5.1 Contexto e Desafio Técnico
No console VMware HTML5 (vSphere / ESXi Web Client), os eventos de teclado são capturados via JavaScript no canvas do navegador e transmitidos via WebSocket para o hypervisor.
Diferente de conexões RDP ou VMRC nativas:
1. O controlador virtualizado do ESXi normalmente emula um teclado US de 101/104 teclas.
2. A tecla física de barra/interrogação do layout ABNT2 (`VK_ABNT_C1`, Scan code `0x73`, browser `code: "IntlRo"`) é frequentemente descartada ou não traduzida pelo console HTML5.
3. Teclas mortas de acentuação (`´`, `~`, `^`, `` ` ``) enviadas sem intervalo pós-soltura (*inter-key delay*) colidem no buffer de eventos do navegador, impedindo a composição correta no sistema operacional convidado.

### 5.2 Roteiro de Calibração da Barra (`/`) e Interrogação (`?`)
1. No VmTyper, abra **Diagnóstico → Aba "Calibração VM (Barra e Acentos)"**.
2. Deixe o console VMware HTML5 visível e com o cursor posicionado em um editor ou terminal da VM.
3. Na seção **"Diagnóstico Rápido da Barra ('/')"**, clique em cada um dos testes individuais:
   - **1. Numpad Divide (E0 35):** envia o scan code `0x35` estendido (teclado numérico).
   - **2. Tecla ABNT2 (Scan 0x73):** envia o scan code `0x73` físico da tecla ao lado do Shift direito.
   - **3. Tecla US (Scan 0x35):** envia o scan code `0x35` não-estendido (barra do layout US de 104 teclas).
   - **4. AltGr + Q:** envia `Ctrl + Alt + Q` (mapeamento secundário em layouts que mapeiam a barra em Q).
   - **5. Alt + 47 (Numpad):** envia a sequência de código de caractere ASCII universal via Alt numpad.
4. Após o disparo de cada teste (com a contagem de 3 segundos para focar no console), anote qual dos candidatos produziu exatamente a barra `/` na tela da VM.
5. Selecione o candidato aprovado no ComboBox **"Candidato físico"** e clique em **"Validado com Sucesso"**.
6. Clique no botão verde **"Salvar Mapeamento Aprovado no Perfil da VM"**. O VmTyper salvará essa regra física em `AppSettings.CharacterSubstitutions`, preservando todas as outras regras existentes.

### 5.3 Roteiro de Calibração de Acentuação (`á, ã, é, ê, í, ó, ô, õ, ú` e maiúsculas)
1. No ComboBox **"Caractere alvo"**, selecione o caractere desejado (ex.: `á`, `ã`, `é`).
2. O VmTyper carrega automaticamente os candidatos pré-definidos:
   - **Candidato 1 (Padrão com intervalo):** Sequência física da tecla morta (ex.: Agudo no scan `0x1A` ou Til no scan `0x28`) com pausa programada de 15ms após a soltura, seguida da descida e subida da letra base.
   - **Candidato 2 (US-International):** Apóstrofo (scan `0x28`) ou Shift+~ (scan `0x29`) com pausa de 15ms + letra base.
   - **Candidato 3 (Alt Numpad):** Sequência física de Alt Down + dígitos numéricos + Alt Up.
3. Clique em **"Testar Candidato na VM (3s)"** e focalize a área de texto do console VMware.
4. Se o caractere acentuado compuser perfeitamente (ex.: `á` em vez de `´a` ou `a`), marque **"Validado com Sucesso"** e clique em **"Salvar Mapeamento Aprovado no Perfil da VM"**.
5. Se por qualquer motivo uma tecla morta ficar pendente na VM (ex.: teste interrompido), clique no botão **"Limpar Teclas Mortas (Flush)"** para enviar a sequência corretiva `Space + Backspace`.

