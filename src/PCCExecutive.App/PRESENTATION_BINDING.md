# PCC Executive App binding seam

Worker 4 intentionally does **not** duplicate Worker 1 domain/application contracts or Worker 3 browser ownership logic.

`IPccExecutivePresentationGateway` is a UI-facing adapter seam only. At integration time, compose an adapter that projects canonical Domain/Application/Browser state into `RuntimeSnapshot` and maps `UiAction` back to authorized application use cases.

Until that adapter exists, `UnavailablePresentationGateway` is used. It exposes no fake health, browser connection, worker state, progress, verified completion, update readiness or success. All operational commands remain disabled.

Required integration invariants:

- `VerifiedCompletion` comes only from the completion engine; `ManagerEstimate` remains separate.
- `CompletionMode.ClosureMode` is used at the canonical 99% closure transition.
- session actions are enabled only when Worker 3 proves PCC ownership; personal Chrome is never exposed as killable.
- session logical names remain `Manager` / `Worker 1..5` across conversation rollover.
- `AttentionItems` include exact reason, required action and exact target location.
- runtime health uses semantic states, including `RateLimited`, `Cooldown`, `Recovering`, `Challenge`, `AdapterUncertain`.
- countdown/timer values are shown only when supplied or safely derived by runtime; this UI does not fabricate them.
- OpenAI API/Hybrid remain unavailable until explicit API configuration exists.

## Live Worker 1 contract alignment

The UI seam was reconciled against Worker 1 PR #4 at head `f807c0978d93f06f930108081eaf0ec118f91d06` without copying those Domain/Application files into this Worker branch. Integration mapping is intentionally direct:

- `PCCExecutive.Domain.VerifiedCompletion.Percent` -> `RuntimeSnapshot.VerifiedCompletion`.
- `PCCExecutive.Domain.ManagerEstimate.Percent` -> `RuntimeSnapshot.ManagerEstimate`.
- `PCCExecutive.Domain.ProjectCompletionMode.Active/ClosureMode/VerifiedComplete/Blocked` -> the matching UI completion mode. `ClosureMode` remains visually distinct from verified completion.
- `PCCExecutive.Domain.AttentionRequest.Reason/RequiredAction/OpenTarget` -> Attention Center `WhatHappened` / one action / exact location projection; richer user-facing explanation may be supplied by the application adapter without changing the canonical request identity.
- `PCCExecutive.Domain.LogicalAgentSession` and `WorkerSlotId` -> stable `Manager` / `Worker 1..5` logical labels. Browser runtime IDs remain separate control targets.
- `PCCExecutive.Application.PccExecutiveOptions` confirms BrowserChat default, OpenAI API disabled by default, max five Workers, ten-second base interval and adaptive pacing.
- `PCCExecutive.Application.ProviderHealth` feeds semantic health projection; browser-specific health/ownership detail is expected from Worker 3 rather than invented here.

The Integration Lead can replace `UnavailablePresentationGateway` with one adapter after Worker contracts converge, with no need to rewrite the visual layer.
