---
description: Unit test and runtime compatibility guidance for the CastCrew test project.
applyTo:
  - "tests/**/*.cs"
  - "tests/**/*.csproj"
---

# Test Instructions

## Test project conventions

- Keep tests under `tests/Jellyfin.Plugin.CastCrew.Tests/`.
- Focus unit tests on deterministic service/normalizer behavior.
- Use `FullyQualifiedName` filtering examples in docs for single-test guidance.

## Runtime compatibility

- Unit tests target `net8.0`, `net9.0`, and `net10.0`; integration tests target `net10.0`.
- Keep `RollForward=Major` in test project unless runtime strategy is intentionally changed.
- If runtime strategy changes, update README and Copilot instructions in the same change.
