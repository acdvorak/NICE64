# NICE64 Local Readability Prototype

This directory contains reusable, in-process C# extraction code used by Browser Live when local readability is selected. It does not alter Browser Live routing or the NICE wire format.

## Production Switch

`picoswitchcontroller.ini` supports `ReadableSourceMode` in the `BrowserLive`
section:

- `AUTO` (default): fetch once, try local extraction, then use the existing
  Direct converter if the local quality gate fails. FrogFind is not contacted.
- `LOCAL`: same local attempt and safe Direct fallback, useful for explicitly
  testing the local path.
- `DIRECT`: use the existing PowerShell Direct converter only.
- `FROGFIND`: retain the existing remote merged-readable path as an explicit
  diagnostic compatibility mode.

The C# fetch stores `original-source.html` and `local-readable-model.json` in
the normal request directory. The existing PowerShell image candidate helpers
consume that model and original HTML to emit the unchanged `document.json`.

## Parser

The fixture runner uses `HtmlAgilityPack` 1.11.62.

- License: MIT, as declared by the package manifest.
- Target compatibility: the runner targets .NET 9 and consumes the parser's `netstandard2.0` assembly.
- Transitive dependencies for that target: none.
- Deployed parser files: one `HtmlAgilityPack.dll` file, 165,376 bytes in the current restore.

Html Agility Pack was selected because it is a tolerant managed HTML DOM parser with XPath-style traversal, has a permissive license, and does not bring a browser engine, JavaScript runtime, or external process into the package.

No FrogFind or FiveFilters source code is used or translated here.

## Model and Limits

`ReadablePageModel` is an internal semantic model. It carries blocks, direct links, card/group context, diagnostics, and extraction quality. It is deliberately not a ROM-facing type.

`NiceReadableOptions` bounds source bytes, DOM nodes, DOM depth, blocks, links, text, candidates, parse time, and extraction time. The extractor accepts a `CancellationToken`; limit hits and cancellation produce a failed quality result rather than an uncontrolled exception.

## Extraction

Article extraction scores `article`, `main`, `section`, and `div` candidates using text, paragraphs, headings, link density, and semantic class/ID signals. Landing extraction instead retains high-quality repeated cards in source order, preserving heading, summary, and action-link relationships.

The AUTO classifier returns `ARTICLE`, `LANDING`, or `SPARSE`. Sparse/static-shell pages fail honestly with `INSUFFICIENT_STATIC_CONTENT` and remain eligible for the existing DIRECT fallback in a future integration.

The extractor does not choose an image. The runner writes `image-match-input.json`, containing card destinations, headings, summaries, and links for the existing destination-aware Browser Live image matcher.

## Fixture Runner

From `tests/BrowserLive.ReadabilityFixtureRunner`:

```powershell
dotnet run -- --all --verify
dotnet run -- --fixture milk-home --mode auto
dotnet run -- --fixture nintendo-home --output .\Output
```

The runner has no network code. It reads only the saved `Fixtures/*.html` corpus and writes ignored output under `Output/<fixture-name>/`:

- `readable-page-model.json`
- `diagnostics.json` and `diagnostics.txt`
- `candidate-containers.json`
- `candidate-cards.json`
- `quality-result.json`
- `image-match-input.json`
- `preview.html`

`summary.json` is written at the output root.

## Future Integration Proposal

The future Browser Live configuration should expose `ReadableSourceMode` with `LOCAL`, `DIRECT`, `FROGFIND`, and `AUTO`. The intended normal AUTO path is `LOCAL -> DIRECT`; FrogFind is a diagnostic or manually forced compatibility source, not a required runtime dependency.
