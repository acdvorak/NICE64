# Local Readability Prototype Results

## Scope

This is an offline, PC-only fixture result set. The runner never fetches the
network and does not invoke the production Direct converter or FrogFind. Their
columns below are deliberately marked `not run`; they are comparison targets
for a future, separately approved integration evaluation.

## Environment

- Bridge target framework: `net9.0-windows`.
- Fixture runner target: `net9.0`.
- Parser: HtmlAgilityPack `1.11.62` (MIT).
- Parser dependency graph for its `netstandard2.0` target: no transitive
  runtime dependencies.
- Current restored parser payload: one `HtmlAgilityPack.dll`, 165,376 bytes.

## Measured Run

Command:

```powershell
dotnet run -- --all --verify
```

All 15 fixtures and the 20 deterministic behavioral checks passed. The run
averaged 3.27 ms per fixture and peaked at 44 ms; all fixture inputs completed
well below the one-second extraction budget.

| Fixture | Local mode | Blocks | Links | Cards | Quality | Total ms | Direct/FrogFind |
| --- | --- | ---: | ---: | ---: | --- | ---: | --- |
| cnn-lite-index | LANDING | 9 | 3 | 3 | pass | 44 | not run |
| duplicate-title | ARTICLE | 2 | 1 | 1 | pass | 2 | not run |
| external-links | ARTICLE | 3 | 1 | 1 | pass | <1 | not run |
| fox-article | ARTICLE | 8 | 1 | 1 | pass | <1 | not run |
| malformed-html | ARTICLE | 4 | 1 | 1 | pass | 1 | not run |
| many-navigation-links | ARTICLE | 4 | 1 | 1 | pass | <1 | not run |
| milk-home | LANDING | 9 | 3 | 3 | pass | <1 | not run |
| nested-cards | LANDING | 9 | 3 | 3 | pass | 1 | not run |
| nintendo-home | LANDING | 6 | 2 | 2 | pass | 1 | not run |
| no-useful-image | ARTICLE | 3 | 1 | 1 | pass | <1 | not run |
| recipe-content | ARTICLE | 9 | 1 | 1 | pass | <1 | not run |
| relative-links | ARTICLE | 4 | 2 | 1 | pass | <1 | not run |
| sparse-js-heavy | SPARSE | 0 | 0 | 0 | expected fail | <1 | not run |
| toms-hardware-article | ARTICLE | 6 | 1 | 1 | pass | <1 | not run |
| wikipedia-article | ARTICLE | 8 | 1 | 0 | pass | <1 | not run |

The generated `Output/<fixture>/diagnostics.json` additionally records source
bytes, nodes visited, parse/extraction milliseconds, candidate counts, retained
text, and limit hits per fixture.

## Interpretation

- **Nintendo:** classified as a landing page, preserves two product/card
  destinations, including the Madden destination used by the existing
  destination-aware image matcher.
- **Milk:** classified as a landing page with three coherent action cards and
  low utility-navigation retention.
- **Fox/CNN/Tom's/Wikipedia:** article or landing structures retain content
  blocks and direct content links while removing utility clutter.
- **Sparse JS shell:** fails honestly with `INSUFFICIENT_STATIC_CONTENT`; a
  future production caller should use the existing Direct fallback.
- **Image matcher compatibility:** each fixture writes `image-match-input.json`
  with source-order card destination, heading, summary, and direct-link data.
  The existing PowerShell image matcher can consume that adapter without a
  format redesign.

## Limits and Deterministic Tests

The verification suite covers article-root selection, landing classification,
repeated cards, anchors nested in semantic blocks, relative and unsafe links,
duplicate suppression, malformed markup, sparse quality failure, navigation
filtering, lists, no-image content, card destinations, source and node limits,
cancellation, extraction timeout, and source-order preservation.

Default limits are 4 MB source, 50,000 DOM nodes, depth 128, 120 blocks, 24
links, 12,000 text characters, 96 candidate containers, one-second parse, and
two-second extraction. The extractor returns controlled diagnostics for every
limit/cancellation path rather than looping or throwing through the caller.

## Known Limits

- Fixtures are deterministic representative saved HTML, not a live-site crawl
  corpus; no claim of parity with today's changing sites is made here.
- This prototype does not execute JavaScript, CSS layout, browser automation,
  or image ranking itself.
- The current card scoring is intentionally conservative. A future integration
  should compare Local versus Direct against captured real pages before making
  Local the default.

## Recommended Next Checkpoint

Add a **disabled-by-default Browser Live integration adapter** that maps this
model into the existing generator model, records Local quality diagnostics, and
falls back to Direct on any quality failure. Do not change the ROM, protocol,
or make FrogFind a required runtime dependency.
