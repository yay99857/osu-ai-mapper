# osu-ai-mapper

Estúdio local para transformar músicas em rascunhos editáveis de osu!standard com
Mapperatorinator. Estado documentado em 15/10/2026.

O aplicativo Avalonia e a CLI compartilham análise de timing, projetos persistentes,
prévia de 30 segundos, controles de estilo, calibração de estrelas, verificação de
mapas, mapsets e exportação `.osz`.

## Começar

Requer .NET 10 SDK, `uv` para a análise Python e o checkout do Mapperatorinator com
seu ambiente em `artifacts/external/Mapperatorinator/.venv`.

```powershell
dotnet run --project apps/desktop -c Release
```

Importe uma música, escolha a pasta do projeto, confira título, artista e mapper,
analise o timing e gere uma prévia antes do mapa completo. BPM e offset podem ser
corrigidos. Imagens de fundo e metadados podem mudar sem regenerar os objetos.
“Abrir no osu!” abre uma cópia exportada pela associação de arquivos do sistema.

```powershell
dotnet run --project apps/cli -c Release -- generate song.mp3 artifacts/projects/song --title "Title" --artist "Artist" --creator "Your name" --difficulty "Insane" --stars 5 --model mapperatorinator:v32
```

O segundo argumento é uma pasta de projeto. Comandos adicionais: `preview`,
`regenerate`, `mapset`, `check`, `package`, `metadata`, `export` e `info`.
Consulte a ajuda da CLI para os argumentos de cada comando.

Estilo: `--descriptors "skillset/jumps,style/clean"`, `--mapper sotarks` ou um ID
conhecido pelo modelo, `--strong-style true` e `--year 2023`.
Timing: `--bpm 174 --offset-ms 412.5`; o offset informado é acústico.
Ctrl+C cancela a etapa em execução.

## Motor e arquivos locais

Mapperatorinator v32 é o único motor de IA adotado para novas gerações.
A integração fornece áudio WAV e um mapa de referência de timing; importa o resultado
pelo adaptador oficial, valida e calcula a dificuldade. Os pesos ficam no cache
Hugging Face, fora de `artifacts` por padrão; o primeiro uso pode precisar de download.
Veja [a arquitetura](docs/architecture.md).

Pulso, B2 e o dataset de treinamento anterior foram aposentados do fluxo do produto.
Seu código e alguns controles de compatibilidade ainda existem no repositório.
Remover os arquivos locais não remove esses caminhos do código. B1 continua necessário
para produzir a referência de timing do Mapperatorinator.

Preserve `artifacts/external/Mapperatorinator` e o cache dos pesos. Projetos novos
contêm áudio gerenciado e revisões: só apague suas pastas se quiser descartá-los.
`artifacts/fetch` pode conter credenciais locais; nunca publique seu conteúdo.
Caches, saídas de testes e instaladores gerados podem ser recriados.

## Estrutura

| Caminho | Responsabilidade |
|---|---|
| `apps/desktop`, `apps/cli` | Interfaces do estúdio |
| `packages/application` | Orquestração, Mapperatorinator e verificação de mapas |
| `packages/project-store` | Manifestos, áudio, análises e revisões |
| `packages/beatmap-core`, `packages/contracts` | IR, schemas e validação estrutural |
| `packages/osu-adapter` | Integração oficial, dificuldade e exportação |
| `engines/audio-analysis` | Decode, transcode, timing e features |
| `engines/generation` | B1, incluindo a referência de timing |
| `tools/installer` | Publicação e instalador Windows |
| `tests/fixtures` | Mapas sintéticos e snapshots |
| `ml`, `tools/dataset-builder` | Código de pesquisa legado; sem dataset obrigatório |

## Verificação e instalador

```powershell
dotnet test OsuAiMapper.slnx
uv run --project engines/audio-analysis pytest engines/audio-analysis
dotnet run --project tools/compatibility-harness -- roundtrip tests/fixtures/osu
pwsh -File tools/installer/build.ps1
```

O instalador atual publica o app self-contained em
`%LOCALAPPDATA%\Programs\osu-ai-mapper`, mas depende dos ambientes Python e do
Mapperatorinator no checkout. `osu-ai-mapper.json` aponta para esse repositório;
mover o checkout exige atualizar a configuração ou reconstruir o instalador.
Ainda não é uma distribuição independente.

Tempos internos usam microssegundos inteiros; beats são racionais; coordenadas usam
o playfield 512×384. Dificuldade sempre carrega o perfil do motor.

## Documentação

- [Estado atual e roadmap](ROADMAP.md)
- [Arquitetura](docs/architecture.md)
- [Decisões atuais](docs/adr/README.md)
- [Compatibilidade](docs/compatibility-matrix.md)
- [Verificação da integração oficial](docs/foundation-status.md)
- [Princípios de mapping](docs/mapping-principles.md)
- [Dados e arquivos locais](docs/data-rights-policy.md)
- [Contexto de mapping](context/README.md)
