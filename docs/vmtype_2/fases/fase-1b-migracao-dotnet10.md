# Fase 1B: Migração .NET 8 → .NET 10 LTS (isolada)

**Por que agora, e não na F6:** o .NET 8 sai de suporte em **10/11/2026**, e a máquina de desenvolvimento só tem o SDK `10.0.401` (com os runtimes Desktop 8.0.31 e 10.0.12). A suíte de regressão da F1 é a rede de segurança. O prompt exige uma etapa isolada, sem misturar com mudanças no motor, e esta fase cumpre isso: **um único PR, sem mudança funcional**.

## Passos
1. **Antes:** rodar a suíte completa e o teste de integração com o receptor, e salvar os resultados (`dotnet test --logger trx`) em `relatorios/F1B-antes/`.
2. Criar `global.json` na raiz:
   ```json
   { "sdk": { "version": "10.0.401", "rollForward": "latestFeature" } }
   ```
3. `VmTyper.csproj` e os projetos de teste: `<TargetFramework>net10.0-windows</TargetFramework>`.
4. Atualizar os pacotes de teste para versões compatíveis com o .NET 10 (fixar no `Directory.Packages.props`).
5. `dotnet build -warnaserror` e tratar avisos novos (analisadores do .NET 10, APIs obsoletas). Avisos de API obsoleta de WPF/WinForms precisam de justificativa para cada um que ficar suprimido.
6. Verificar: `UseWindowsForms` (ícone de bandeja), `ApplicationHighDpiMode`, `app.manifest` e P/Invoke (`Marshal.SizeOf(INPUT)` continua 40 em x64; existe um teste para isso).
7. **Depois:** a mesma suíte e o mesmo corpus no receptor. Comparar com o "antes": mesmo número de testes, mesmos resultados e texto recebido idêntico.
8. Teste manual curto: abrir o app, digitar um bloco no Bloco de Notas, abrir as configurações e a bandeja.
9. Atualizar o README §3 (SDK necessário e caminho do executável `bin\x64\Release\net10.0-windows\`).

## Critérios de aceite
- [ ] Mesma contagem de testes, todos com o mesmo resultado antes e depois.
- [ ] Receptor: texto idêntico.
- [ ] Zero avisos novos sem justificativa.
- [ ] Diff do PR restrito a csproj, global.json, pacotes e README.

## Reversão
Reverter o PR único. Nenhum formato de arquivo nem contrato mudou.

## Opcional (avaliar e registrar em ADR, sem fazer junto)
Publicação self-contained / single-file (`PublishSingleFile`, `SelfContained`) para não depender do runtime instalado nas máquinas corporativas.
