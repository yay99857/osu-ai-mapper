# Estado atual e roadmap

Atualizado em 15/10/2026. Mapperatorinator v32 é o motor de IA adotado.
O treinamento próprio, Pulso e o dataset antigo saíram do plano do produto.

## Implementado

- App desktop Avalonia e CLI usando serviços compartilhados.
- Projetos persistentes com áudio gerenciado, análise em cache e revisões.
- Análise de timing com correções de BPM e offset.
- Integração local do Mapperatorinator, prévia dos 30 segundos mais fortes,
  estilo por descritores, mapper conhecido e ano.
- Progresso, cancelamento, estimativa de duração e calibração por resultados anteriores.
- Relatório de problemas, linha do tempo, fundo, metadados e mapsets.
- Importação/exportação pelo adaptador oficial e cálculo de dificuldade fixado.
- Instalador Windows self-contained para o app, ainda dependente do checkout e do Python.

Isso descreve o código existente; não representa uma nova execução dos testes ou
uma avaliação recente da qualidade dos mapas.

## Próximos passos

| Prioridade | Entrega | Critério de conclusão |
|---|---|---|
| 1 | Encerrar a exposição dos modelos aposentados | UI e CLI orientam novas gerações ao Mapperatorinator; projetos antigos recebem mensagem clara se seus pesos não existem |
| 2 | Simplificar o setup | Dependências e caminhos verificados antes da geração, com diagnóstico acionável |
| 3 | Validar qualidade em músicas novas | Comparações de ritmo, sliders, densidade e flow, acompanhadas de revisão humana |
| 4 | Melhorar controle de dificuldade e estilo | Alvo e resultado visíveis; mudanças avaliadas sem esconder falhas de timing ou trechos vazios |
| 5 | Tornar a instalação independente do checkout | Runtime e localização dos recursos definidos; instalação, cancelamento e desinstalação verificados |
| 6 | Edição assistida por trecho | Preservar objetos fora do intervalo e revisões anteriores; validar transições |

B1 permanece como infraestrutura da referência de timing. Remover seu código exige
primeiro substituir essa dependência. Código de pesquisa pode ser retirado em uma
mudança específica, com revisão das referências e dos testes.

## Avaliação e recursos

Use fixtures sintéticas para compatibilidade e referências humanas autorizadas quando
uma mudança de mapping precisar de comparação. O dataset antigo não é requisito
para abrir o app ou usar Mapperatorinator. Não reutilize números de benchmarks antigos
como evidência do motor atual.

Neste PC de 16 GB, execute apenas um processo pesado por vez. Não rode geração,
separação de áudio ou análise em lote em paralelo.
