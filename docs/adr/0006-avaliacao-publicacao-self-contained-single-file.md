# ADR 0006: Avaliação de publicação self-contained / single-file no .NET 10

- **Status:** Proposto (avaliado na Fase 1B, sem ativar no `.csproj` padrão)
- **Data:** 2026-11-09
- **Fase:** F1B (Migração .NET 8 → .NET 10 LTS)

## Contexto

Com a migração do `VmTyper` para `net10.0-windows` (`win-x64`), estações de trabalho corporativas onde o operador executa o aplicativo podem não ter o *Microsoft Windows Desktop Runtime 10.0* pré-instalado. Além disso, políticas corporativas frequentemente restringem instaladores de sistema que exigem elevação administrativa.

Por outro lado, ativar `<SelfContained>true</SelfContained>` e `<PublishSingleFile>true</PublishSingleFile>` diretamente no `VmTyper.csproj` afetaria o ciclo de build/teste diário e o tamanho dos artefatos de desenvolvimento.

## Opções consideradas

1. **Manter apenas publicação dependente do framework (`SelfContained=false`)**
   - *Prós:* binário enxuto (~300 KB), build rápido.
   - *Contras:* exige instalação prévia do Desktop Runtime 10.0 na máquina do operador.
2. **Forçar `SelfContained=true` e `PublishSingleFile=true` fixos no `VmTyper.csproj`**
   - *Prós:* um único `.exe` sempre gerado sem depender de runtime instalado.
   - *Contras:* aumenta o tempo de compilação e o tamanho de saída em `Debug`/`Release` (~140–170 MB para WPF + WinForms), além de exigir `IncludeNativeLibrariesForSelfExtract=true` para extrair as DLLs nativas de WPF (`wpfgfx_cor3.dll`, `PresentationNative_cor3.dll`, `D3DCompiler_47_cor3.dll`). Em ambientes com EDR restritivo, a autoextração em `%TEMP%\.net\` pode sofrer bloqueio ou varredura lenta no primeiro início.
3. **Manter `VmTyper.csproj` enxuto e parametrizar via perfil de publicação (`dotnet publish -r win-x64 --self-contained`) na Fase 6**
   - *Prós:* preserva builds e testes rápidos durante o desenvolvimento (F1B–F5); permite oferecer duas variantes de distribuição na F6:
     - *Framework-dependent* (para máquinas com .NET 10 Desktop Runtime);
     - *Self-contained* em pasta ou *single-file* com `IncludeNativeLibrariesForSelfExtract=true` e `EnableCompressionInSingleFile=true` (~65–80 MB comprimido), sem `PublishTrimmed` (pois trimming não é suportado por WPF/WinForms devido ao uso de reflexão em bindings XAML).

## Decisão

Adotar a **Opção 3**:
1. Não alterar o `VmTyper.csproj` na Fase 1B com `SelfContained` ou `PublishSingleFile`, mantendo a migração 1B estritamente isolada à troca de TFM (`net10.0-windows`).
2. Na Fase 6 (Qualidade e empacotamento), disponibilizar um perfil `Properties/PublishProfiles/Portable-win-x64.pubxml` com:
   - `RuntimeIdentifier=win-x64`
   - `SelfContained=true`
   - `PublishSingleFile=true`
   - `IncludeNativeLibrariesForSelfExtract=true`
   - `EnableCompressionInSingleFile=true`
   - `PublishTrimmed=false` (obrigatório para WPF).

## Consequências

- O diff da Fase 1B permanece mínimo e reversível.
- A distribuição portátil sem instalador fica documentada e pronta para ser empacotada na Fase 6, levando em conta o comportamento de autoextração de DLLs nativas do WPF em máquinas com EDR.
