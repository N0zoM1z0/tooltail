# Windows Shell Manual Test Matrix

## Purpose

This matrix is the interactive evidence gate for M4. Automated tests prove deterministic lease state, target identity, native event delivery, coordinate math, manifest settings, accessibility structure, and own-HWND styles. They cannot prove every real desktop focus, monitor, taskbar, virtual-desktop, remote-session, or assistive-technology interaction.

Never automate display reconfiguration, close a user's existing application, send global input, or start an elevated target for this matrix. Use disposable windows and an attended test session. Record the exact build commit, Windows build, display topology, scale, input method, and result.

## Automated baseline

| Check | Current evidence |
| --- | --- |
| standard-user manifest | static test requires `asInvoker` and `uiAccess=false` |
| Per-Monitor V2 | static manifest test plus apphost runtime awareness gate |
| transparent ambient surfaces | XAML architecture test and Windows rendered shell smoke |
| pet non-activation | `WS_EX_NOACTIVATE`, `WM_MOUSEACTIVATE=MA_NOACTIVATE`, and foreground-not-pet smoke gate |
| transparent pet pixels | `WM_NCHITTEST=HTTRANSPARENT` outside the bounded visible sprite region |
| tether input pass-through | `WS_EX_TRANSPARENT`, no activation, no WPF hit testing |
| target eligibility | pure precedence tests plus real synthetic HWND enumeration/own-process exclusion |
| identity anti-reuse | HWND/root HWND/PID/process-start tests before issue and during reconciliation |
| tracking and disposal | real location/destroy hooks, dedicated message loop, 20 repeated register/dispose cycles |
| end-to-end native lease | real synthetic HWND attach, move reconciliation, and destroy revocation |
| common scale math | exact 100%, 125%, 150%, and 200% physical/DIP round trips |
| negative/rotated geometry | exact negative-origin and independent-axis coordinate tests |
| keyboard alternatives | static accessible-control tests for attach, inspect, revoke, pause, cancel, and home |
| existing-folder grant boundary | static picker/preview/confirm surface, portable identity/race/expiry persistence tests, native current-user DPAPI test, and apphost restore/revoke smoke |
| prior WPF surfaces | baseline, Skill Card, and Agent Body render smokes |

The synthetic HWND is created with no activation and destroyed by the test itself. No existing host window or process is modified or stopped.

## Interactive target matrix

For each target, attach by drag and by the keyboard picker, inspect the exact HWND/PID/start identity, move/resize/minimize/restore it, revoke, and close the disposable target. Confirm the tether never intercepts typing, pointer input, title-bar controls, resize borders, or task content.

| Target | Normal | Maximized | Snapped | Minimize/restore | Close/revoke | Keyboard attach | Status |
| --- | --- | --- | --- | --- | --- | --- | --- |
| File Explorer on a disposable folder | required | required | required | required | required | required | NOT RUN |
| Windows Terminal with no active command | required | required | required | required | required | required | NOT RUN |
| Visual Studio or disposable editor window | required | required | required | required | required | required | NOT RUN |
| Browser on a blank/offline page | required | required | required | required | required | required | NOT RUN |
| Windows Settings | required | required | required | required | required | required | NOT RUN |
| disposable WPF sample window | automated synthetic native coverage plus interactive render | required | required | required | automated | required | PARTIAL |

Expected result: exactly one visible context lease exists after a successful explicit drop/attach. Pulling the pet away, choosing Unbind/Home, target close, identity change, or timeout removes the tether and records one terminal reason. No folder grant appears.

## Display and session matrix

| Scenario | Required observation | Status |
| --- | --- | --- |
| single monitor at 100% | physical outline and pet anchor align | NOT RUN |
| single monitor at 125% | physical outline and pet anchor align | NOT RUN |
| single monitor at 150% | physical outline and pet anchor align | NOT RUN |
| single monitor at 200% | physical outline and pet anchor align; text remains usable | NOT RUN |
| mixed 100%/150% | move target both directions; no jump/drift | NOT RUN |
| mixed 125%/200% | move target both directions; no jump/drift | NOT RUN |
| secondary monitor left/above primary | negative origins remain aligned | NOT RUN |
| portrait/rotated monitor | outline follows the physical rectangle | NOT RUN |
| taskbar on each edge | home and anchored pet remain in work area | NOT RUN |
| monitor disconnect/reconnect | reconcile or revoke visibly; no stale off-screen tether | NOT RUN |
| virtual desktop switch | hidden/cloaked target revokes or reconciles visibly | NOT RUN |
| remote-session connect/resize | physical layout reconciles without focus theft | NOT RUN |

Do not change the host's display configuration without the machine owner's explicit consent. Pure coordinate tests remain evidence for unavailable topologies, not a substitute for marking an interactive row PASS.

## Focus and accessibility matrix

| Scenario | Expected result | Status |
| --- | --- | --- |
| click visible pet without dragging | explicit inspector opens; target was not activated by ambient surface first | NOT RUN |
| click transparent pet corner | underlying target receives the click | NOT RUN |
| click/type through tether and outline | target remains fully usable | NOT RUN |
| stress move/resize for five minutes | tracking settles without drift, callback leak, or focus theft | NOT RUN |
| keyboard-only attach/inspect/revoke/home | every action is reachable with visible focus | NOT RUN |
| keyboard-only pause/cancel with no run | truthful no-active-run result; no fake state change | NOT RUN |
| keyboard-only existing-folder select/confirm/revoke | selection creates no authority; exact root/capabilities are announced before confirmation; revoke remains reachable | NOT RUN |
| screen reader on Home/Inspector | names, scope, reason, identity, times, and controls are announced | NOT RUN |
| Windows high contrast | outline, labels, body geometry, and focus remain legible | NOT RUN |
| reduced motion | no critical information or control disappears | NOT RUN |
| 200% text scaling | Inspector/Home remain scrollable and usable | NOT RUN |
| pet hidden/occluded | Home/Inspector controls remain usable | NOT RUN |

## Security checks

| Scenario | Expected result | Status |
| --- | --- | --- |
| Tooltail-owned Home/Inspector/Pet/Tether under pointer | discovery skips them | automated PASS |
| transient tool/transparent/child/shell surface | no lease issues | policy PASS; interactive NOT RUN |
| higher-integrity or unverifiable target | neutral rejection; no target behind it is selected | policy PASS; attended elevated-target test NOT RUN |
| HWND reused with a different PID/start | active lease revokes instead of transferring | automated PASS |
| title changes only | display updates; identity and authority do not change | automated PASS |
| monitor/hook ends unexpectedly | lease visibly revokes `target_ineligible` | automated PASS |

## Result record

For every interactive run, append a dated record containing:

```text
Commit:
Windows build/edition:
Standard-user confirmation:
Display topology and scales:
Session type (local/RDP):
Assistive settings:
Rows executed:
PASS/FAIL per row:
Observed focus owner before/after ambient interaction:
Lease terminal reason:
Known defects and reproduction:
```

M4 must remain active while any required interactive row is unrun or failed. Do not convert automated math or style checks into a claimed manual PASS.
