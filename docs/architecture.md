# Arquitetura atual

Atualizado em 15/10/2026.

## Fluxo

Desktop e CLI → serviços de aplicação → projeto persistente → análise Python →
referência de timing B1 → Mapperatorinator → importação oficial → validação,
relatório e dificuldade → revisão → exportação de mapset.

- `packages/application/MapperatorinatorEngine.cs` executa o motor como subprocesso.
- `packages/application/MappingService.cs` prepara referência, áudio e revisões.
- `packages/project-store` guarda manifestos, áudio, análises e revisões.
- `packages/osu-adapter` concentra as dependências de `osu.Game`.
- `engines/audio-analysis` roda pelo `uv`; o motor usa seu próprio Python.

A referência passa como contexto de timing. Ritmo e posicionamento finais vêm do
Mapperatorinator. A integração trata argumentos Hydra com aspas, descarta objetos fora
de ordem e pode refazer trechos vazios com outra semente.

## Recursos locais

O checkout do motor e seu ambiente ficam em
`artifacts/external/Mapperatorinator`. Os pesos v32 ficam no cache Hugging Face,
respeitando `HF_HOME` quando definido. Preserve ambos para evitar reinstalação ou
novo download. Arquivos em `artifacts` são ignorados pelo Git.

Pulso/B2 e as ferramentas de treino ainda possuem código de compatibilidade, mas
seus modelos locais e dataset não são dependências do fluxo atual. B1 continua sendo
uma dependência técnica do timing.

## Integridade

Metadados e imagens podem mudar sem gerar objetos novamente. Análises são vinculadas
ao áudio e às configurações. Geração cria revisões; cancelamento deve preservar a
última revisão válida. Exportação reúne as dificuldades selecionadas no projeto.

Importar no osu! usa uma cópia do pacote exportado. Não escreva diretamente no
armazenamento do cliente. Perfis de formato e dificuldade estão na
[matriz de compatibilidade](compatibility-matrix.md).
