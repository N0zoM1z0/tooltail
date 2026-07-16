# M6 Evaluator Checklist

## Purpose and evidence boundary

This checklist runs the formative Tooltail studies without confusing engineering evidence with participant evidence. Automated tests prove contract, safety, and deterministic behavior; only an attended session with recorded consent can produce a usability result. Every participant row starts as **NOT RUN** and must remain so until the named evaluator actually completes it.

The product research mode is optional, local, and off by default. It records only the closed `research-event.schema.json` fields. Before opt-in, show the participant the exact disclosure in Home. Screen recording, interviews, think-aloud notes, and quotations are outside Tooltail and require separate consent and storage approved by the study owner.

Never enter a participant name, email address, username, machine identity, real filename, real path, window title, prompt, transcript, or free-form note into research events. Use the random study/session IDs shown in Home to join an exported JSONL file to separately governed study notes.

## Reproducible starting state

### Attended Windows Desktop session

1. Run as a standard user on a supported Windows 11 machine; do not elevate Tooltail.
2. Confirm Home says `OFF — no workflow study events are recorded` and that no opt-in is preselected.
3. Ask the participant to choose **Opt in locally** only after reading the disclosure. If they decline, continue the product evaluation without event logging.
4. Choose **Create safe lab and grant**. This creates a new grant-ID directory below Tooltail-owned local application storage and three Tooltail-authored synthetic PDFs. It does not select an arbitrary user folder.
5. For a later task block, choose **Reset study fixture**. This starts a new random session and salt, revokes the exact current lab grant if one exists, and creates a new safe lab. It never removes or rewrites the prior lab.
6. Use **Preview exact JSONL** before export. Use **Export reviewed JSONL** only if the participant has agreed to hand off the local file.
7. At withdrawal or session end, choose **Delete all research data**. Verify Home shows OFF, zero events, and an empty preview. Export files owned by this research store are truncated so an already shared copy must be handled under the external study protocol.

The Desktop reset is deliberately non-destructive. It does not erase durable skills, receipts, or prior lab files and must not be described as a clean application reinstall. A truly clean first-launch evaluation requires a fresh standard-user profile or a separately prepared clean application-data environment.

### Headless engineering fixture

Use the fixture CLI only with a new absolute path that does not exist. A reset means choosing another new path; never recursively clear or reuse the old workspace.

```powershell
$run = [guid]::NewGuid().ToString('N')
$fixture = "D:\tmp\tooltail-study-$run"
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- init-fixture --workspace $fixture --name "File invoice PDFs"
```

For the complete deterministic engineering corpus:

```powershell
$run = [guid]::NewGuid().ToString('N')
dotnet run --project tools/Tooltail.SkillFixtureCli -c Release -- golden-suite --workspace "D:\tmp\tooltail-study-golden-$run"
```

The CLI proves the headless loop; it is not participant evidence. Preserve or dispose of marked fixture workspaces only under the lab's external retention procedure.

## Study A — body comprehension

Use the fixed simulator catalog and randomize presentation order across participants. Include `needs_input`, `failed`, `permission_revoked`, `working`, `parallel_work`, `completed_receipt`, and at least one neutral state. Test embodied-only, inspector-only, and combined views in counterbalanced order; repeat with reduced motion and high contrast.

For each state, record outside the product:

- the participant's selected closed state label;
- whether they believe Tooltail can act now;
- their selected next-action category;
- recognition latency in milliseconds;
- presentation condition and order.

Pass criteria are the thresholds in `TEST_AND_RESEARCH_PLAN.md`: at least 85% state accuracy, median recognition of interruption states within three seconds, and no critical state distinguishable only by color. Record confusion pairs; do not rewrite an answer after explaining the state.

## Study B — scope and revocation mental model

Ask the participant to:

1. explain what a WindowLease provides;
2. explain what the exact folder ResourceGrant provides;
3. predict five closed examples: inspect context, enumerate granted folder, move within granted root, affect another folder, and inject global input;
4. unbind the window context;
5. revoke the folder grant;
6. predict whether the old plan, approval, execution, and Undo can still run;
7. inspect the permission-revoked body and exact Inspector fields.

Critical errors are: treating a WindowLease as mutation authority, assuming all-window/all-folder access, believing revocation deletes files, or believing an old fingerprint remains executable. Target thresholds are 80% fully correct critical-scope answers and 90% unassisted revocation.

## Study C — teach, correct, reuse, and Undo

Use only the synthetic safe-lab PDFs. The participant should complete these checkpoints without a live model or Codex process:

1. start teaching;
2. create `Invoices` and demonstrate the same safe move/rename on two synthetic PDFs;
3. stop and reconcile;
4. answer only the typed compiler clarification;
5. inspect the Draft Skill Card and state what may happen;
6. rehearse and inspect the exact unapproved production plan;
7. approve the exact fingerprint and inspect the verified receipt;
8. plan, separately approve, and verify Undo;
9. create correction v2 and explain the semantic diff;
10. predict the changed v2 plan, rehearse it, approve it, and inspect its receipt;
11. export the authority-free Companion Capsule.

Record closed task success, reason code, assistance count, prediction correctness, and whether the participant describes v2 as learned/corrected behavior. Do not use think-aloud timing in the final comparison round. A correction passes causal review only when its semantic diff matches the actual changed plan.

## Capsule preference and instance value

After successful corrected reuse, ask the participant to choose one closed preference:

- keep this taught companion/capsule;
- replace it with an identical untaught instance;
- no preference.

Then ask whether they would export the capsule before a reset. Store the 1–7 participant rating through Home only if consent remains active. Qualitative explanations remain outside the product under separate consent. Treat capsule preference as a directional longitudinal signal, not proof of launch readiness.

## Local export review

Before handing off JSONL, verify every non-empty line parses under `research-event.schema.json` and visually confirm the absence of raw names, paths, content, titles, prompts, transcripts, user/machine identifiers, and credentials. The expected fields are random IDs, sequence, UTC time, closed type, success, enum-like reason code, and optional bounded duration/count/version/body/token/rating.

There is no automatic upload. Copying or sending the reviewed export is an external researcher action and must follow the study's consent and retention policy.

## Results ledger

Create a dated research note only after an actual attended session. Record protocol version, build commit, Windows version, display/accessibility condition, random study/session IDs, completed checklist rows, deviations, and consented aggregate results. Keep every unrun row as **NOT RUN**. Never infer participant thresholds from apphost smoke, simulator output, unit tests, or the fixture CLI.

Current repository result: **NOT RUN — no participant study has been conducted or recorded by this implementation work.**
