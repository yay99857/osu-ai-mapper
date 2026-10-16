# Generates a map for every constant-tempo song of a dataset, one project per song, for comparison
# with tools/map-stats. Analyses are shared through a content-addressed cache, so rerunning with
# other settings does not analyse again.
#   pwsh tools/map-stats/generate_all.ps1 artifacts/dataset artifacts/eval/b1-5 -Stars 5
#   pwsh tools/map-stats/generate_all.ps1 artifacts/dataset artifacts/eval/b2-6 -Stars 6 -RhythmModel artifacts/models/rhythm-b2.joblib
param(
    [Parameter(Mandatory)] [string] $Dataset,
    [Parameter(Mandatory)] [string] $Output,
    [double] $Stars = 5,
    [string] $RhythmModel,
    [string] $AnalysisCache = "artifacts/cache"
)

$maps = Get-Content (Join-Path $Dataset "manifests/maps.jsonl") | ForEach-Object { $_ | ConvertFrom-Json } | Where-Object status -eq "accepted"

foreach ($group in $maps | Group-Object audioSha256) {
    $m = $group.Group[0]
    $ir = Get-Content (Join-Path $Dataset $m.irRef) -Raw | ConvertFrom-Json
    if ($ir.timing.segments.Count -gt 1) { continue }

    $out = Join-Path $Output $m.audioSha256.Substring(0, 12)
    $label = if ($RhythmModel) { "B2" } else { "B1" }
    $flags = @("--title", $m.title, "--artist", $m.artist, "--creator", "osu-ai-mapper", "--difficulty", $label, "--stars", "$Stars", "--analysis-cache", $AnalysisCache)
    if ($RhythmModel) { $flags += @("--rhythm-model", $RhythmModel) }

    $command = if (Test-Path (Join-Path $out "project.json")) { @("regenerate", $out) } else { @("generate", (Join-Path $Dataset $m.audioRef), $out) }
    $result = dotnet run --project apps/cli -c Release --no-build -- @command @flags 2>&1 | Select-String "^stars|^error"
    Write-Output ("{0,-40} {1}" -f $m.title.Substring(0, [Math]::Min(40, $m.title.Length)), $result)
}
