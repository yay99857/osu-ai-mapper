# Decisões de arquitetura atuais

Atualizado em 15/10/2026. Este documento substitui o registro do pipeline de treino
aposentado e reúne as decisões que orientam o produto atual.

| Decisão | Motivo e consequência |
|---|---|
| Mapperatorinator v32 como motor de IA | O produto concentra-se no estúdio de geração e revisão; não requer treinamento próprio |
| B1 mantido para timing | A integração precisa do mapa de referência; aposentadoria do Pulso não autoriza remover essa dependência |
| Avalonia/.NET para desktop | Reutiliza os serviços C# da CLI e o domínio existente |
| Processos Python separados | Análise via uv e motor com ambiente próprio; cancelamento deve encerrar os filhos |
| IR próprio e adaptador oficial | Domínio independente da UI; só osu-adapter depende de osu.Game |
| Versões oficiais fixadas | Resultados de dificuldade pertencem a um perfil; upgrades exigem revisão dos snapshots |
| Projetos com revisões | Áudio gerenciado, cache vinculado ao conteúdo e preservação da última revisão válida |
| Integração externa com osu! | Exportar/importar pacotes por ação do usuário, sem alterar o storage pessoal |
| Processamento local | Dependências e pesos podem exigir rede no setup; geração usa recursos locais |
| Compatibilidade legada explícita | Código Pulso/B2 ainda presente não significa que seus pesos existam ou sejam parte do fluxo adotado |

A matriz de compatibilidade, os schemas e os testes continuam sendo referências
técnicas. Veja [a arquitetura](../architecture.md) e [o roadmap](../../ROADMAP.md).
