# PCC Executive — Wave 2 Visible Control Census

TASK: `PCCEXECUTIVE-T0001`  
SCOPE: WPF runtime binding / dead-control closure / visual acceptance  
LAW: every visible operational control is `WIRED_REAL`, `DISABLED_WITH_REASON`, or `INFORMATIONAL_ONLY`.

`WIRED_REAL` means the command terminates at a real accepted runtime/application contract; its `CanExecute`
may still be false when the required live object/evidence is absent. `DISABLED_WITH_REASON` means the UI
intentionally exposes the approved product control but prevents invocation because the owning Worker service
does not yet exist in the composed candidate. No local fake state is substituted.

| SCREEN | CONTROL | COMMAND | BACKEND_SERVICE | STATE_SOURCE | ENABLED_RULE | ERROR_PATH | CURRENT_STATUS |
|---|---|---|---|---|---|---|---|
| Chrome Connection | Connect / Recover Chrome | ConnectChrome | Worker 3 BrowserSessionController | Browser runtime registry | Browser runtime composed | Inline UI error | WIRED_REAL |
| Chrome Connection | Open Browser / Bring to Front | OpenSession / BringSessionToFront | Worker 3 BrowserSessionController | Manager runtime identity | Manager runtime exists | Inline UI error | WIRED_REAL |
| Chrome Connection | Retry Health | RetryHealth | Worker 3 semantic adapter refresh | ChatGPT semantic snapshot | Runtime binding composed | Inline health state | WIRED_REAL |
| Project Selection | Resolve Project | ResolveProject | PR #7 IProjectControlResolver | Live PCC routing | Non-empty project/alias | ProjectResolutionStatus | WIRED_REAL |
| Project Selection | Open Project | SelectProject | PR #7 resolver + baseline builder | PCC/GitHub baseline | Routed project identity | ProjectResolutionStatus | WIRED_REAL |
| Executive Dashboard | Operational metrics | — | Read-only presentation mapping | RuntimeSnapshot | Evidence supplied only | Unknown shown as — | INFORMATIONAL_ONLY |
| Manager Workspace | Open / Bring to Front | OpenSession / BringSessionToFront | Worker 3 BrowserSessionController | Manager runtime identity | Runtime exists | Inline UI error | WIRED_REAL |
| Manager Workspace | Pause / Resume AI | PauseAi / ResumeAi | Worker 1 orchestration command service | ProjectRun state | Service not composed | Disabled reason | DISABLED_WITH_REASON |
| Manager Workspace | Request Plan | RequestManagerPlan | Worker 1 Manager coordinator | Manager/wave state | Service not composed | Disabled reason | DISABLED_WITH_REASON |
| Manager Workspace | Conversation History | ToggleConversationHistoryCommand | Presentation lineage inspection | RuntimeSnapshot.Conversations | Always | No mutation | WIRED_REAL |
| Workers Dispatch | Start / Resume / Pause | StartDispatch / PauseDispatch | Worker 1 dispatch/orchestration service | Dispatch scheduler | Service not composed | Disabled reason | DISABLED_WITH_REASON |
| Workers Dispatch | Dispatch Mode | SelectedDispatchMode | Worker 1 dispatch service | Dispatch settings | Editable only with dispatch service | Disabled reason | DISABLED_WITH_REASON |
| Worker Chat | Select Worker | SelectWorkerCommand | Presentation selection | RuntimeSnapshot.Workers | Worker exists | No backend mutation | WIRED_REAL |
| Worker Chat | Open / Front / Hide / Restart | Session actions | Worker 3 BrowserSessionController | Worker runtime identity | Runtime exists | Inline UI error | WIRED_REAL |
| Worker Chat | Kill Session | KillSession | Worker 3 ownership proof + KillAsync | Positive PCC ownership proof | CanKill=true | Confirmation + inline error | WIRED_REAL |
| Wave Summary | Reconcile & Send to Manager | ReconcileWave | Worker 1 reconciliation service | Wave/handoff evidence | Service not composed | Disabled reason | DISABLED_WITH_REASON |
| Task Board | Filters | Local filter properties | Presentation filtering | Canonical task snapshot | Always | No mutation | WIRED_REAL |
| Task Board | Task state cards | — | Read-only presentation mapping | Canonical TaskState | No local edits | Unknown remains unknown | INFORMATIONAL_ONLY |
| Evidence & Verification | Run Verification | RunVerification | Completion-gate/evidence service | Persisted completion gates | Service not composed | Disabled reason | DISABLED_WITH_REASON |
| Evidence & Verification | PCC/HEAD/PR/CI/freshness | — | PR #7 live evidence mapping | PCC/GitHub baseline | Evidence supplied only | Stale/unknown explicit | INFORMATIONAL_ONLY |
| Loop Guard | Inspect / Replan / Resume Once / Stop | Loop actions | Worker 1 LoopGuard/orchestration | Loop snapshots | Service not composed | Disabled reason | DISABLED_WITH_REASON |
| ChatGPT Health & Recovery | Retry Health | RetryHealth | Worker 3 semantic adapter | Semantic health snapshot | Runtime binding composed | Inline state/Attention | WIRED_REAL |
| Session Monitor | Open / Front / Hide / Restart | Session actions | Worker 3 BrowserSessionController | Runtime registry | Runtime exists | Inline UI error | WIRED_REAL |
| Session Monitor | Kill | KillSession | Worker 3 ownership proof + KillAsync | Positive ownership proof | CanKill=true | Confirmation + inline error | WIRED_REAL |
| Session Monitor | Kill All PCC Sessions | KillAllPccSessions | Worker 3 KillAllPccSessionsAsync | Positive ownership proofs | At least one owned runtime | Confirmation; unproven skipped | WIRED_REAL |
| Settings | Provider / dispatch controls | SelectedProviderMode / SelectedDispatchMode | Worker 2 durable settings | Persisted settings | Persistence not integrated | Disabled reason | DISABLED_WITH_REASON |
| Settings | Save Settings | SaveSettings | Worker 2 durable settings repository | Persisted settings | Persistence not integrated | Disabled reason | DISABLED_WITH_REASON |
| Update Center | Check for Updates | CheckForUpdates | Configured manifest source | PCC_EXECUTIVE_UPDATE_MANIFEST | Source exists | Manifest state | WIRED_REAL |
| Update Center | Install Update & Restart | InstallUpdateAndRestart | Worker 5 staged-install contract | Package/backup/migration readiness | Contract not composed | Disabled reason | DISABLED_WITH_REASON |
| Attention Center | Open Exact Place | OpenAttentionLocation | Worker 3 session foreground action | Active attention runtime identity | Item active | Inline UI error | WIRED_REAL |

## P0 unresolved-dead-control result

There are **zero P0 controls with an unclassified/silent no-op state** in this census. P0 controls are either
wired to real runtime services with evidence-gated `CanExecute`, or visibly disabled with the exact missing
service/evidence reason. Disabled controls are not counted as feature-complete.

## Dependency notes

- Worker 1 PR #4 / PR #7: Domain/Application, PCC routing and GitHub evidence read-side contracts.
- Worker 3 PR #5 / PR #8: PCC-owned browser/session controls and ChatGPT semantic resilience.
- Worker 2 durable persistence is not available in live state; settings/startup recovery remain disabled/unclaimed.
- Worker 5 PR #6 / PR #9 owns packaging/update execution; Wave 2 only reads configured update metadata and
  keeps installation disabled until the staged-install execution contract is composed.
