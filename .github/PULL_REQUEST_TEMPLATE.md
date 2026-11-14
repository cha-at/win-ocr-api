## What changed

<!-- One or two sentences. Link the issue if there is one: "Closes #12". -->

## Why

<!-- The problem this solves. For behaviour changes, say what the old behaviour was. -->

## How it was tested

<!--
No CI yet, so paste the evidence:
- `dotnet build -c Release` result
- for endpoint changes: the request and the response
- for the AI engine: GET /diag/ai output and the machine's Windows build
-->

## Checklist

- [ ] `dotnet build -c Release` passes with no new warnings
- [ ] Behaviour changes were exercised against a running instance
- [ ] `README.md` and `README.zh-CN.md` updated if the API changed
- [ ] `CHANGELOG.md` entry added under `## [Unreleased]`
- [ ] No build output, certificates or secrets included
- [ ] The change is compatible with existing clients (or the breaking change is explained above)
