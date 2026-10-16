# Dados e arquivos locais

Músicas, mapas de terceiros, pesos e credenciais permanecem fora do Git.
`.gitignore` exclui `artifacts/`, `data/` e pacotes `.osz`.

## Recursos atuais

- Preserve o checkout e o Python do Mapperatorinator em `artifacts/external/Mapperatorinator`.
- Preserve o cache Hugging Face dos pesos para reutilizar a instalação.
- Projetos guardam cópias de áudio e revisões; excluir um projeto descarta esse trabalho.
- Caches de análise e instaladores gerados são recriáveis.
- `artifacts/fetch` pode conter credenciais: nunca imprima seu conteúdo nem o publique.
- O dataset e os pesos Pulso/B2 antigos não são requisitos do fluxo atual.

A licença do código de um motor não estabelece a licença dos pesos ou das músicas.
Qualquer distribuição deve verificar as permissões específicas dos arquivos envolvidos;
a documentação não concede autorização de redistribuição.

## Referências e cliente osu!

Para avaliações futuras, registre origem, permissões e identidade dos arquivos usados.
Não restaure o treinamento próprio ou um corpus grande como requisito implícito.

O aplicativo exporta pacotes e abre cópias por ação do usuário. Ferramentas e testes
não devem alterar diretamente Realm, arquivos com hash ou a biblioteca pessoal do osu!.
