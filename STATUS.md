## 2026-04-22 — Replicate safety-filter retry

- Added `ReplicateSafetyException` and detect Flux's `E005` (input/output flagged as sensitive) in `ReplicateImageService.PollForOutputAsync`.
- New `GenerateImageWithSafetyRetryAsync` in `ArtGenerationService` catches the safety exception and asks Claude to rewrite the prompt before retrying. Up to 2 safety retries per quality attempt; separate from the 3-attempt scoring budget.
- `SanitizePromptAsync` softens on retry 1; on retry 2 it strips the hidden mythical-creature section and any predator/prey cues. Falls back to the original prompt if Claude returns empty.
- Status UI shows "Sanitizing prompt (safety retry N/2)…" while it works.
