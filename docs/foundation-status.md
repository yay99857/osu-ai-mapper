# Verificação da integração oficial

Atualizado em 15/10/2026. Este documento cobre interoperabilidade; o estado do produto
fica no [roadmap](../ROADMAP.md).

## Cobertura existente

O harness executa parse → IR → encode → parse → dificuldade sem abrir o cliente.
As doze fixtures sintéticas em `tests/fixtures/osu` cobrem timing constante e variável,
offset negativo, sliders, samples, kiai, Unicode, stacking e formatos antigos/modernos.
Os snapshots ficam em `tests/fixtures/golden`.

Registros anteriores reportaram round-trip das 12 fixtures sem deriva de estrelas,
com perda declarada de nome de sample customizado, e teste manual de importação pelo
usuário em 01/10/2026 no cliente `2026.921.0-lazer`.
Esses resultados são históricos; não foram reproduzidos nesta revisão documental.

## Verificar

```powershell
dotnet test OsuAiMapper.slnx
dotnet run --project tools/compatibility-harness -- roundtrip tests/fixtures/osu
dotnet run --project tools/compatibility-harness -- demo artifacts/demo
```

Para testar no cliente, copie `artifacts/demo/osu-ai-mapper-demo.osz`, importe a
cópia e confira áudio, objetos, sliders e logs. Registre a data, a versão do cliente
e o resultado. Testes headless não comprovam reprodução e importação no cliente real.

Ao mudar os pacotes oficiais, siga a [matriz](compatibility-matrix.md).
Fixtures sintéticas devem continuar disponíveis sem o dataset antigo.
