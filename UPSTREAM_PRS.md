# Upstream PR drafts for BrianHumlicek/NeoHub

> **Drafts only.** Nothing in this file has been submitted upstream. Nothing may be
> submitted (no PR, issue or comment) without the owner's explicit OK.

This fork is upstream `master` (61a1a0e) plus these commits, in this order:

| Commit subject | Upstream status |
|---|---|
| `Remove the exact disconnected WebSocket client` | Upstream PR #45, carried unchanged (section B) |
| `Add arm_home_instant / arm_away_instant WebSocket commands` | Draft PR (section A) |
| `Add "Stay Instant" arm button to the home page` | Optional second commit of the same draft PR (section A) |
| `Add UPSTREAM_PRS.md with upstream PR drafts` | Fork only, never submit |
| `Carry the panel response code through SessionResponse and PanelCommandResult` | Draft PR (section D) |
| `Bound the panel answer wait for PanelCommandService commands at 8 s` | Draft PR (section D) |
| `Show "Partition not ready to arm (zone open)" for arm rejections with code 1` | Draft PR (section D); its text is made neutral by section D commit 11 |
| `Answer WebSocket commands that carry a request_id with command_result` | Draft PR (section D) |
| `Send the partition ready state to WebSocket clients` | Draft PR (section D) |
| `Add upstream PR draft D: command results and partition ready state` | Fork only, never submit |
| `Report a panel interface NACK as a failed command_result` | Draft PR (section D) |
| `Redact access codes from logged client messages` | Draft PR (section D) |
| `Keep a failed command_result send out of the generic error path` | Draft PR (section D) |
| `Send ready as false while the partition is not disarmed` | Draft PR (section D) |
| `Leave bypass programming after an unanswered ConfigurationEnter` | Draft PR (section D) |
| `Update upstream PR draft D after review` | Fork only, never submit |
| `Use the neutral text "Partition not ready to arm" for arm rejections with code 1` | Draft PR (section D, commit 11) |
| `Read the partition trouble flag in the connect-time status pull` | Draft PR (section E) |
| `Send the partition trouble flag to WebSocket clients` | Draft PR (section E) |
| `Let SessionCommand callers log their own failures` | Draft PR (section E) |
| `Track PanelCommandService commands in progress per session` | Draft PR (section E) |
| `Re-read the partition trouble flag periodically` | Draft PR (section E) |
| `Re-read the trouble flag right after an arm is rejected with code 1` | Draft PR (section E) |
| `Log undecoded panel notifications once per command word` | Draft PR (section E) |
| `End a trouble refresh round when the panel does not answer` | Draft PR (section E) |
| `Add upstream PR draft E: partition trouble state` | Fork only, never submit |
| `Rate-limit trouble read warnings per partition` | Draft PR (section E) |
| `Log when the trouble refresh pauses for programming mode` | Draft PR (section E) |
| `Do not dump undecoded payloads of access code commands` | Draft PR (section E) |
| `Update upstream PR draft E after review` | Fork only, never submit |
| `Warn once per session while trouble reads keep failing` | Draft PR (section E) |
| `Update upstream PR draft E for the session-wide warning` | Fork only, never submit |

Section C records why panic / fire / auxiliary activation (`ModuleControl_Activate_FAP`, 0x0908) is not proposed.
Section D is a separate draft PR (command results, panel response codes, an 8 s bound on the panel's answer, partition ready state). It builds on section A.
Section E is a separate draft PR (partition trouble flag kept current and sent to clients, log of undecoded panel notifications). It builds on section D.

---

## A. Add arm_home_instant / arm_away_instant WebSocket commands

- **Proposed title:** `Add arm_home_instant / arm_away_instant WebSocket commands`
- **Base branch:** `master`
- **Commits to cherry-pick, in this order, onto a new branch made from upstream `master`:**
  1. `Add arm_home_instant / arm_away_instant WebSocket commands` (required)
  2. `Add "Stay Instant" arm button to the home page` (optional; leave it out if the maintainer wants API changes only)

  Do not include `Remove the exact disconnected WebSocket client` (that is PR #45) or the UPSTREAM_PRS.md commit.
  Both commits were cherry-picked onto upstream `master` 61a1a0e without PR #45 in a scratch clone:
  they apply without conflicts, and the solution builds with 14 warnings and 0 errors, the same warnings as `master`
  (only the source offset of the existing Home.razor `MaxWidth` MUD0002 warning moves, because of the added button).
- **Issue #54:** the issue (read through a page summarizer on 2026-11-17) asks for (1) new client messages `arm_home_instant`
  (`StayArmWithNoEntryDelay`) and, optionally, `arm_away_instant`, and (2) optionally, an extra field in
  `partition_update` / `PartitionDto` showing the active arming mode. This PR does item 1 only, so the body
  says "Related to #54".

### PR body

````markdown
## Summary

Adds two WebSocket client messages that arm a partition with the existing `ArmingMode` values `StayArmWithNoEntryDelay` and `AwayArmWithNoEntryDelay`:

| `type` | `ArmingMode` sent to the panel |
|---|---|
| `arm_home_instant` | `StayArmWithNoEntryDelay` (7) |
| `arm_away_instant` | `AwayArmWithNoEntryDelay` (8), untested on a real panel |

The payload is the same as for `arm_home` / `arm_away`:

```json
{"type": "arm_home_instant", "session_id": "<session id>", "partition_number": 1, "code": null}
```

`code` is optional. When it is missing or null, the connection's default access code is used, as for `arm_home`.

An optional second commit adds a "Stay Instant" button to the home page, next to Away / Stay / Night, with the same enable condition.

Related to #54. This implements item 1 of that issue; the optional `partition_update` field from item 2 is not part of this PR.

## Motivation

The only arm messages in the WebSocket API are `arm_away` (AwayArm), `arm_home` (StayArm) and `arm_night` (NightArm). Clients such as the Home Assistant integration therefore cannot use the "Stay Instant" mode (`ArmingMode.StayArmWithNoEntryDelay`) or its Away counterpart, although TLink's `ArmingMode` already has both.

## Changes

- `Api/WebSocket/Models/WebSocketMessage.cs`: new records `ArmHomeInstantMessage` and `ArmAwayInstantMessage`, both deriving from `ArmCommandMessage` and registered with `[JsonDerivedType]` as `arm_home_instant` / `arm_away_instant`.
- `Api/WebSocket/PanelWebSocketHandler.cs`: two new `case`s that call the existing `HandleArmCommandAsync` with mode 7 / mode 8.
- Optional commit, `Components/Pages/Home.razor`: a "Stay Instant" button that calls `ArmPartitionAsync(..., ArmingMode.StayArmWithNoEntryDelay)`.

There are no TLink changes and no changes to existing message types, their mappings or `ArmDisarmNotificationHandler`.

## Test evidence

Tested on a PowerSeries Neo panel (HS2032 class) with a TL280-family communicator, ITv2 with Type 1 encryption. The arm commands were sent from NeoHub's Dev Console as `ModuleControl Partition Arm Control` with a valid user code, not through the new WebSocket messages:

- `StayArmWithNoEntryDelay` (7) was accepted, and the partition armed with no exit-delay notification (a normal Stay arm on the same panel shows an exit delay). The panel then reported plain `StayArm` (1) in `NotificationArmDisarm`, so the partition shows `armed_home`.
- `InstantStayArm` (17) was rejected with response code 2 (`InvalidProgrammingType`). This is why `arm_home_instant` uses mode 7.
- `AwayArmWithNoEntryDelay` (8) has not been sent to a real panel.

Offline checks:

- `dotnet build NeoHub.sln`: 14 warnings and 0 errors, the same warnings as `master` (with the optional Home.razor commit, only the source offset of the existing Home.razor `MaxWidth` MUD0002 warning moves).
- A throwaway console harness (not part of this PR) deserialized messages with the handler's own `_jsonOptions` and called `ProcessMessageAsync` with fake services:
  - `arm_home` → `StayArm` (1), `arm_away` → `AwayArm` (2), `arm_night` → `NightArm` (4); `disarm` and `bypass_zone` route as before.
  - `arm_home_instant` → mode 7 and `arm_away_instant` → mode 8. `code` is passed through, and is null when it is missing.
  - Everything sent to clients (`partition_update` for every status value, `zone_update`, `error`, `full_state`) is byte-identical to `master`.
- `ArmDisarmNotificationHandler` already maps 7 → `armed_home` and 8 → `armed_away`.

Untested:

- The new WebSocket messages end to end on a real panel (the panel test above used the Dev Console).
- Mode 8 / `arm_away_instant` on any real panel.
- Entry-delay behaviour of modes 7 and 8: not observed. "No entry delay" is only the name of the `ArmingMode` values.
- The Home.razor button on a real panel.

## Compatibility

- Additive only. Existing messages map exactly as before, and the partition status values sent to clients are unchanged. A Stay Instant arm shows as `armed_home`, so clients cannot tell it apart from a normal Stay arm (that is item 2 of #54).
- An older NeoHub answers the new types with an `error` message whose text is `Invalid JSON: Read unrecognized type discriminator id 'arm_home_instant'. ...` (the apostrophes are sent escaped as `\u0027`), and does not arm. Clients should only send them to a NeoHub version that has them.
- As for every existing message, `type` must be the first JSON property, because .NET 8 `System.Text.Json` polymorphic deserialization only reads the discriminator there. The Home Assistant integration's client already sends it first.
````

### Checklist for the owner before submitting

- [ ] The owner has given an explicit OK to open this PR.
- [ ] Re-read #54. Use "Closes #54" only if item 2 (the `partition_update` mode field) is dropped or moved to its own issue; otherwise keep "Related to #54".
- [ ] Fetch upstream `master`. If it has moved past 61a1a0e, cherry-pick onto the new tip, resolve any conflicts and rebuild.
- [ ] Use a dedicated branch for the PR (not `toronto`), so the PR #45 and UPSTREAM_PRS.md commits stay out.
- [ ] Decide whether the `Co-Authored-By` / `Claude-Session` trailers stay in the commit messages sent upstream.
- [ ] Decide whether the Home.razor commit goes in.
- [ ] Do the planned local test: arm with `arm_home_instant` through the WebSocket API (for example from Home Assistant), check that the panel arms and the partition shows `armed_home`, then disarm. Update "Test evidence" and "Untested".
- [ ] Mode 8: either test `arm_away_instant` on the panel and update the text, or submit it marked untested, or drop it from the PR.
- [ ] Check the PR text, logs and screenshots for personal data: no names, addresses, panel or installer IDs, IP addresses, session IDs or access codes.

---

## B. PR #45: Remove the exact disconnected WebSocket client

- Already open upstream by its own author (base `master`, 1 commit, head cd93304; open and without reviews when checked on 2026-11-17).
- This fork carries it unchanged as the commit `Remove the exact disconnected WebSocket client`, a cherry-pick of cd93304 with the same tree and patch.
- **Nothing to submit.** Do not open a duplicate PR.
- Optional: a short supportive comment on #45, only with the owner's OK, and only stating what was actually observed (for example that the fork carries it unchanged and it builds on `master`; add runtime results only after they have been seen).
- When upstream merges or changes #45, drop this commit when the fork is rebased on the new `master`.

Checklist:

- [ ] The owner has given an explicit OK before any comment on #45.
- [ ] Any comment claims only observed results.
- [ ] After an upstream merge, rebase `toronto` and drop the cherry-pick.

---

## C. Not proposed: ModuleControl_Activate_FAP (0x0908)

**Decision: not implemented and not proposed upstream.** No reliable source (working code with a verifiable origin, or protocol documentation) gives the payload of 0x0908. Sending a guessed layout could report the wrong emergency type to the monitoring station.

### What exists

- **The enum value only.** `ModuleControl_Activate_FAP = 2312, // 0x0908` in NeoHub and, since 60851d9 (2024-09-09), in DSC-TLink. Per the research, the enum came with a set of ITv2 definitions whose header (DSC-TLink `ITv2_Integration_Options.txt`) names a vendor library, `TSP.Intrusion.Server.ITV2ProtocolLibrary`. No revision of NeoHub, DSC-TLink, their forks or the NeoHub Home Assistant integration defines, sends, tests or documents a 0x0908 message.
- **One encoder, classified as unverified inference.** LawPaul/HA_DSC_Neo_ITv2 `messages.py`@776b170 (lines 1974-2001) encodes `[cmd_seq u8][CompactInt partition][u8 type]` with fire = 0, auxiliary = 1, panic = 2 and no access code. It is not treated as reliable because:
  - its cited source, "dsc-itv2-protocol observations" (rmilecki/dsc-itv2-protocol), has no FAP / 0x0908 content in any of its 17 commits (the npm `dsc-itv2-client` package has none either);
  - the type values are marked "per panel manual", but no manual is named;
  - the sibling 0x0902 layout added in the same file was wrong and had to be fixed later (pcanto a284e01);
  - the FAP block is byte-identical in every checked commit of the repo and its forks (8+ commits), so it was never revisited;
  - its only test is an encode/decode round-trip with no captured bytes, and no document says it was tested on a panel.
- **No 0x0908 in independent ITv2 clients:** npm `dsc-itv2-client` (all 32 published versions, 1.0.7-2.1.0) and the Bentel client (Shikaban/bentel-absoluta-local, master 2eac1a5 and dev ecbe9dd). Vendor ITv2 integration guides (victor 5.2 / 5.6, C-CURE 9000 ITv2 v2.8 and later) list no fire / auxiliary / panic operation. A vendor app set-up document (Connect Alarm) only says that the app can raise a Panic alarm, over a cloud connection that uses "ITv2 and Fibro protocols at the same time"; it does not say which protocol or command carries the panic.

### What is unknown

- **Payload layout:** no source.
- **Type values:** three numberings are in circulation, and none applies to ITv2 0x0908:
  - LawPaul 0 / 1 / 2: unsourced (above);
  - IT-100 serial / Envisalink TPI command 060 with '1' to '3': a different protocol and transport;
  - npm `dsc-itv2-client` AlarmType 4 / 10 / 11: a field of the inbound 0x0840 notification, not of the 0x0908 command.
- **Access code:** unknown. LawPaul sends none, but that is the author's design, not panel evidence. The generic response codes include `InvalidAccessCode` (17) and `AccessCodeRequired` (18), but nothing ties them to 0x0908.
- **Response:** only the generic mechanism is documented, in NeoHub's reverse-engineered protocol doc. A command is answered by `Command_Response` with a `CommandResponseCode`, either synchronously or as a `SimpleAck` followed later by the response. `Command_Error` is a communicator-level protocol NACK; panel (business-logic) rejects arrive as `CommandResponse` codes. (The command IDs, `Command_Error` = 0x0501 and `Command_Response` = 0x0502, come from NeoHub's `ITv2Command.cs`, not from the doc.) The actual reply to 0x0908, and the notifications that follow it, are undocumented.

### Response-behaviour caveat

In LawPaul issue #12 (discussion about the sibling command 0x0915, relayed through a page summarizer, not documentation), wrong body layouts got only a transport `Simple_Ack`, with no `Command_Response`, no `Command_Error` and no zone change: "So a wrong layout fails silently". Issue #11 shows a 0x0902 rejection with code 3 (`UnsupportedModule`). A malformed 0x0908 may therefore fail silently, and response codes cannot be relied on to detect a wrong layout or type value.

### What a safe live test would require

- The owner's explicit decision to test a hypothesis layout (for example LawPaul's), knowing that it is inference.
- Coordination with the monitoring station before the test: the account is put on test, the station confirms it will not dispatch, and afterwards the station confirms which event type (fire / auxiliary / panic) and partition it received, because a wrong type byte could report a different emergency.
- The owner present at the panel, with the disarm / silence procedure ready; one type at a time, on one partition.
- Full logging of the transmitted bytes and of everything that comes back: `Simple_Ack`, `Command_Response` code, `Command_Error`, and follow-up notifications such as 0x0841 (`ModuleStatus_Miscellaneous_NonZone_Alarm_Status`) and 0x023B (`Notification_Partition_Audible_Bell`, which NeoHub does not register and so receives as a `DefaultMessage`).
- Reading the result correctly: a `Simple_Ack` with no `Command_Response` is not success, and no non-zero code is documented for 0x0908.

An authoritative answer would instead need a capture of a vendor tool sending 0x0908, or the vendor library's 0x0908 command class. Obtaining or decompiling that is a licensing decision for the owner and was not done.

### Sources

As recorded in the research notes; "(inference)" and "(discussion)" mark sources that are not reliable evidence for the 0x0908 payload.

- https://github.com/BrianHumlicek/NeoHub NeoHub/TLink/ITv2/Enumerations/ITv2Command.cs:158 @61a1a0e (enum line); ITv2Command.cs:53 (0x023B), :148 (0x0841); ITv2Command.cs:77-78 (`Command_Error` 0x0501, `Command_Response` 0x0502)
- https://github.com/BrianHumlicek/DSC-TLink src/TLink/ITv2/ITv2Command.cs:139 @60851d9 (enum line, first added); vendor-library header in ITv2_Integration_Options.txt
- https://github.com/BrianHumlicek/DSC-TLink references/Connect-Alarm-Panel-Set-Up.pdf @94fddac (pdftotext -layout lines 165, 190-193; vendor app set-up document: the app can raise a Panic, cloud connection via "ITv2 and Fibro"; no protocol, command or payload)
- https://github.com/LawPaul/HA_DSC_Neo_ITv2 custom_components/dsc_neo_itv2/dsc_itv2/messages.py:1536-1545,1948-2010 @776b170 (inference); FAP class at messages.py:1974-2001, "per panel manual" at messages.py:1978; __init__.py:69-73,91,202-216 and docs/services.md:43-56,67 @776b170 (no code field; inference/design); README.md@776b170:188-190 ("Individual modules cite where each detail came from"); test_messages.py:35 (round-trip only); docs/known-limitations.md:69 (only warns that activate_fap reports to the central station)
- https://github.com/pcanto/HA_DSC_Neo_ITv2 commit a284e01 (sibling 0x0902 layout fix)
- https://github.com/rmilecki/dsc-itv2-protocol @aaf2dce (17 commits, no FAP content)
- https://github.com/Shikaban/bentel-absoluta-local master 2eac1a5, dev ecbe9dd (Bentel ITv2 client; command codes 2304, 2305, 2306, 2322, no 2312)
- npm dsc-itv2-client 2.1.0 src/ITV2Client.js:2032-2039 (AlarmType of the 0x0840 notification, not FAP); https://registry.npmjs.org/-/v1/search?text=itv2 lists only dsc-itv2-client and homebridge-dsc-neohub
- https://github.com/openhab/openhab-addons bundles/org.openhab.binding.dscalarm/.../DSCAlarmCode.java:45-46 @542c1aa (IT-100 / TPI, different protocol); crates.io testlive 0.2.0 src/alarminterface/dsc_commands.js:41-51 (IT-100, different protocol)
- https://github.com/BrianHumlicek/NeoHub docs/TLink-ITv2-Protocol.md:472-473,845 @61a1a0e (documentation, reverse-engineered, generic); lines 804-869 (CommandError is a communicator-level NACK, panel rejects are CommandResponse codes)
- NeoHub NeoHub/TLink/ITv2/Enumerations/CommandResponseCode.cs:24-73 @61a1a0e (generic codes; 3 = UnsupportedModule at :33-34; 17/18 at :42-46)
- Vendor integration documents in the public S3 bucket https://s3.amazonaws.com/compatibilitymatrix?prefix=CCURE9000MatrixDocuments/DSC/ and ?prefix=TSP%20Compatibility%20Matrix/victor/DSC/ (77 Neo documents; e.g. DSC_Neo_UserGuide_v3.20.pdf, pdftotext lines 2480-2486: the virtual keypad offers only arm / disarm, bypass / reset and output activate / deactivate)
- https://github.com/LawPaul/HA_DSC_Neo_ITv2/issues/12 "Virtual inputs" (open, 2026-11-09; discussion, 0x0915, wrong layouts fail silently with only a Simple_Ack)
- https://github.com/LawPaul/HA_DSC_Neo_ITv2/issues/11 (2026-10-16; discussion, 0x0902 rejected with code 3)
- https://github.com/BrianHumlicek/NeoHub/issues/42 (discussion, physical keypad F/A/P presses, not 0x0908)

---

## D. Correlated command results, panel response codes and the partition ready state

- **Proposed title:** `WebSocket: command_result for commands with a request_id, and partition ready state`
- **Base branch:** `master`, after section A (or together with it; see below).
- **Commits to cherry-pick, in this order, onto a branch that already has section A's first commit:**
  1. `Carry the panel response code through SessionResponse and PanelCommandResult`
  2. `Bound the panel answer wait for PanelCommandService commands at 8 s`
  3. `Show "Partition not ready to arm (zone open)" for arm rejections with code 1`
  4. `Answer WebSocket commands that carry a request_id with command_result`
  5. `Send the partition ready state to WebSocket clients`
  6. `Report a panel interface NACK as a failed command_result`
  7. `Redact access codes from logged client messages`
  8. `Keep a failed command_result send out of the generic error path`
  9. `Send ready as false while the partition is not disarmed`
  10. `Leave bypass programming after an unanswered ConfigurationEnter`
  11. `Use the neutral text "Partition not ready to arm" for arm rejections with code 1`

  Commits 6-10 came out of review and build on 1-5. Commit 11 replaces the code-1 text of commit 3 with a neutral one,
  because code 1 can have other causes; keep both, or squash them and use commit 11's subject. Do not include PR #45 or
  the UPSTREAM_PRS.md commits.
- **Dependency on section A:** the eleven commits were cherry-picked in a scratch clone onto upstream `master` 61a1a0e plus
  `Add arm_home_instant / arm_away_instant WebSocket commands`: no conflicts, and the solution builds with 14 warnings and
  0 errors, the same warnings as `master`. Onto plain 61a1a0e, commits 1-3 apply cleanly, but commit 4 conflicts in
  `Api/WebSocket/Models/WebSocketMessage.cs`, because its context includes the `arm_home_instant` / `arm_away_instant`
  lines. So either submit this after section A is merged, or resolve that conflict by hand (and then drop the
  instant-arm types from the PR text).
- **TLink is touched:** `SessionResponse` (public record) gains `int? PanelResponseCode` and `string? PanelNack`.
  `SessionMediator` sets `PanelResponseCode` in one line, and for a `Command_Error` answer logs a warning and sets
  `PanelNack` (the NACK code's name) while keeping `Success = true` as before. The `CommandResponseCode` and `ITv2NackCode`
  enums are not renamed or changed. Say this clearly in the PR, since it changes the library's public surface.
- **Independent parts:** commits 1-3 are useful on their own (numeric code for callers, no unbounded wait, a clear UI
  message). If the maintainer prefers smaller PRs, they can go first as their own PR, and commits 4-10 follow.

### PR body

````markdown
## Summary

WebSocket clients can now learn the outcome of a command and whether a partition is ready to arm:

- Commands (`arm_away`, `arm_home`, `arm_night`, `arm_home_instant`, `arm_away_instant`, `disarm`, `bypass_zone`) accept an optional `request_id`. When it is set, the server answers that client, and only that client, with exactly one `command_result`.
- When the panel rejects a command, the numeric panel response code is kept (`SessionResponse.PanelResponseCode`, `PanelCommandResult.PanelResponseCode`) instead of only its description text.
- When the panel interface refuses a command with a NACK (`Command_Error`), `command_result` reports a failure. `SessionMediator` still returns success for it, as before, so the replies without `request_id` and the web UI do not change.
- An arm command rejected with response code 1 is shown as "Partition not ready to arm" in `command_result` and in the home page's error snackbar.
- `PanelCommandService` waits at most 8 s for the panel's answer to each command it sends. Today an unanswered command waits forever.
- `full_state` and `partition_update` carry the partition's ready state (`PartitionState.IsReady` while the partition is disarmed, otherwise `false`), and `full_state` lists `capabilities` so clients can detect these additions.
- The WebSocket handler no longer writes access codes to the log: the lines that log the raw client JSON replace every `code` value with `***`.

Clients that send no `request_id` get the same replies as before.

## Motivation

- A client cannot tell whether an arm or disarm worked: failures produce an `error` message with nothing tying it to the request, and success produces nothing. An automation that arms the alarm cannot report a refusal, for example because a door is open.
- The panel's answer to "arm while a zone is open" is response code 1, whose TLink description is a long generic list ("Cannot Exit Configuration/Ignore Firmware/..."). Users need "not ready to arm".
- NeoHub already tracks `IsReady` (the home page uses it to enable the arm buttons), but WebSocket clients cannot see it.
- TLink's command receivers have no timeout. If the panel never answers, `ArmAsync` / `DisarmAsync` never return, and the WebSocket handler, which awaits each command inline, stops reading that client's messages.

## Protocol additions

Client command, with the new optional field (`type` must stay the first property, as for every message):

```json
{"type": "arm_home", "session_id": "<session id>", "partition_number": 1, "code": null, "request_id": "<client id, at most 64 characters>"}
```

Server answer, to the requesting client only, exactly once per command that has a `request_id`:

```json
{"type": "command_result", "request_id": "<same>", "success": false, "code": 1, "message": "Partition not ready to arm"}
```

| Outcome | `success` | `code` | `message` |
|---|---|---|---|
| Panel accepted | `true` | `null` | `null` |
| Panel rejected | `false` | panel response code (JSON integer) | its description; `"Partition not ready to arm"` for code 1 on any arm command |
| Panel interface refused it with a NACK (`Command_Error`) | `false` | `null` (a NACK code is not a panel response code) | `"Command refused by the panel interface (NACK: <name>)"`, for example `SystemBusy` |
| Session not found, missing access code, transport error, exception | `false` | `null` | the existing error text (`"Processing error: ..."` for an exception) |
| No panel answer within 8 s | `false` | `null` | `"No confirmation from the panel within 8 s; check the partition state"` (the panel may still have acted) |

- With a `request_id`, no `error` message is sent for that command.
- A `request_id` longer than 64 characters is refused with an `error` message, and the command is not run.
- A message that cannot be deserialized still gets the generic `error` message, because its `request_id` cannot be read reliably. That includes a `request_id` that is not a JSON string: the command is then not run (an older server ignores the unknown property and runs it).
- If sending the `command_result` fails (for example because the client has just disconnected), the failure is logged as a warning; no `error` message is sent instead.

State messages:

- `full_state`: each partition gains `"ready": true|false`; the message gains `"capabilities": ["command_result", "partition_ready"]`.
- `partition_update` gains `"ready": true|false`. `PanelStateService.UpdatePartition` raises `PartitionStateChanged` on every update, so a ready-status change without a status change is already broadcast; it now carries the value.
- `ready` is `PartitionState.IsReady` while the partition's effective status is `disarmed`, and `false` otherwise (armed, exit delay, entry delay, alarm, unknown). `IsReady` only changes on ready-status notifications, which the panel sends while disarmed, so after a live arm it keeps its last value (usually `true`); the status pull at startup already sets it to `false` while armed. `PartitionState` and the home page are unchanged.

## Changes

- TLink `ITv2/MediatR/SessionResponse.cs`, `SessionMediator.cs`: `PanelResponseCode` (`int?`, because `CommandResponseCode` is internal) is set when a `CommandResponse` rejects the command. `PanelNack` (`string?`, the name of the internal `ITv2NackCode`) is set when the answer is a `Command_Error`; `Success` stays `true` for it. `ErrorMessage` and `ErrorCode` are unchanged.
- `Services/IPanelCommandService.cs`: `PanelCommandResult` gains `PanelResponseCode`, `UserMessage`, `DisplayMessage` (`UserMessage ?? ErrorMessage`), `PanelNack` and `TimedOut`, and a `Rejected(...)` factory.
- `Services/PanelCommandService.cs`: a `CancellationTokenSource` of 8 s per command, passed to `SessionCommand` and to `WaitAsync`. Because `SessionMediator` turns the cancellation into a failed response, the timeout is detected from the service's own token. `ArmAsync` sets `UserMessage` for code 1. `PanelNack` is passed through on success. When `ConfigurationEnter` times out, `BypassZoneAsync` sends a best-effort `ConfigurationExit` (the panel may have entered user bypass programming late) and still returns the enter's result. Other `SessionCommand` callers (configuration reads, Dev Console, user and access-code services) are unchanged.
- `Components/Pages/Home.razor`: the arm error snackbar shows `DisplayMessage`.
- `Api/WebSocket/Models/WebSocketMessage.cs`: `ICorrelatedCommandMessage` (`RequestId`, `MaxRequestIdLength = 64`) on `ArmCommandMessage` and `BypassZoneMessage`; new `CommandResultMessage` (`command_result`); `Ready` on `PartitionDto` and `PartitionUpdateMessage`; `Capabilities` on `FullStateMessage`.
- `Api/WebSocket/PanelWebSocketHandler.cs`: `RunCommandAsync` / `SendCommandOutcomeAsync` send either the old `error` (no `request_id`) or one `command_result` (a NACK gives a failed one; a failed send is logged, not turned into an `error`); `request_id` length check; `ready` (via `ReadyForClients`) and `capabilities` in `full_state` / `partition_update`; `RedactAccessCodes` for the three lines that log the raw client JSON (trace line for every received message, invalid JSON, processing error). The redaction also covers invalid or cut-off JSON: numbers, escaped quotes, unterminated strings and flat arrays or objects.

## Compatibility

- Additive. Without a `request_id`, every reply is the same as before, byte for byte, including the `error` text for a code-1 arm rejection (the friendly text is only in `command_result` and the UI) and nothing at all for a NACK.
- One behaviour change for all clients: a command the panel does not answer now fails after 8 s (`error`, or `command_result`), where it used to wait forever. A `bypass_zone` whose `ConfigurationEnter` goes unanswered also sends a `ConfigurationExit`, so it fails after up to 16 s.
- Clients should send `request_id` only when `full_state` lists `command_result` in `capabilities`. Older servers ignore the unknown property, so a client that sends it anyway gets the old replies.
- Clients that read `full_state` / `partition_update` strictly must accept the new `ready` and `capabilities` properties.

## Test evidence

Panel (PowerSeries Neo, HS2032 class, TL280-family communicator): arming with `StayArmWithNoEntryDelay` while a door was open was rejected three times with response code 1, and accepted once the door was closed. That is the basis for the code-1 text, which is applied to all arm modes. The text names no cause, because code 1 may also come back for other "not ready" causes (for example a trouble that blocks arming), which could not be tested.

Offline:

- `dotnet build NeoHub.sln` after every commit: 14 warnings and 0 errors, the same warnings as `master` (compared without line numbers).
- A throwaway console harness (not part of this PR) ran the real `PanelWebSocketHandler`, `PanelCommandService`, `PanelStateService` and TLink `SessionMediator`, with a fake `IMediator` that forwards `SessionCommand` to that `SessionMediator` and a fake `IITv2Session` that plays the panel:
  - 69 command scenarios without `request_id` (each command type with success, code 1, code 17, code 18, default code, missing code, unknown session, transport failure, exception, `Command_Error`; invalid JSON; unknown type): replies byte-identical to the base build, also after commits 6-10.
  - With `request_id`, for every command type: success, unknown session, missing access code (disarm / bypass), panel code 1 (arm types get the friendly text, disarm / bypass the description), panel code 17, transport failure, exception, `Command_Error` NACK (`success: false`, `code: null`, "Command refused by the panel interface (NACK: ...)"; a NACK code 1 is not turned into the code-1 text). Exactly one `command_result`, never an `error`, and `code` is a JSON integer.
  - Timeouts with a fake session that never answers: every command type fails after 8.0 s, also when the session ignores the cancellation token, except `bypass_zone` with an unanswered `ConfigurationEnter`, which sends `ConfigurationExit` and fails after 16.0 s with the same message. Bypass where the write and the exit both go unanswered: 16.0 s.
  - `command_result` reaches only the requesting socket; `request_id` of 64 characters accepted, 65 refused without running the command.
  - `full_state` has `capabilities` and `ready`; a ready-status notification with no status change broadcasts `partition_update` with the new `ready`.
  - TLink `MessageReceiver`: after the wait is cancelled, a late panel answer is consumed by the cancelled receiver without an exception while that receiver is still registered; a retry gets its own answer, and both receivers are then removed as completed. If another routed packet has removed the cancelled receiver first, the late answer is published as an unsolicited notification, which NeoHub has no handler for, and the next command works.
  - A partition armed by a live arm notification, with no new ready-status notification, is sent with `ready: false`.
  - With a `request_id`, a reply send that throws gives no `error` message and no Error log line with the request.
  - Over a real `WebSocket` pair, 400 `partition_update` broadcasts overlapping 100 `command_result` replies arrived intact (500 valid JSON frames, 100 distinct request ids).
  - No access code appears in any frame or in any log line of `PanelWebSocketHandler`, including the invalid-JSON and processing-error lines. The redaction was checked on valid JSON, a numeric code, whitespace and newlines around the colon, escaped quotes, an unterminated string, a code with a comma or brace inside, flat arrays and objects, upper-case keys and duplicate keys.

Untested:

- `command_result`, the 8 s bound and `ready` on a real panel.
- Whether the panel NACKs arm or disarm commands at all, and the best-effort `ConfigurationExit` after an unanswered `ConfigurationEnter`.
- `ready` right after a disarm is the last `IsReady` value from before arming until the panel sends a ready-status notification; whether it sends one on disarm is unverified.

## Known limitations (not changed here)

- The handler still processes one message at a time per client, so a second command waits behind the first: up to 8 s per panel command, and up to three 8 s waits for `bypass_zone` (enter, write, exit).
- A `Command_Error` (communicator NACK) is still a success for `SessionMediator` callers, so the web UI and clients without `request_id` treat it as before (no error shown); only `command_result` reports it.
- When the 8 s bound cancels a command, `SessionMediator` still logs "Error handling command" with a `TaskCanceledException` at Error level, followed by `PanelCommandService`'s warning.
- TLink still logs access codes below the default Information level: the Trace line `TX bytes: {Bytes}` in `ITv2Session` dumps each plaintext packet, which carries the access code in BCD (for example `43-21`), and the Debug line `RX detail: {Message}` writes every public property of each decoded inbound message, so the user codes in an `AccessCodeReadResponse` (read by `PanelUserService`) are logged in full (found by reading `MessageLog`, not observed in a log).
- The redaction recognises only the literal key `"code"` (any letter case). A key written with JSON escapes (for example `"\u0063ode"`) or a nested array value is not fully redacted.
````

### Checklist for the owner before submitting

- [ ] The owner has given an explicit OK to open this PR.
- [ ] Section A is merged upstream, or submitted first; otherwise resolve the `WebSocketMessage.cs` conflict by hand and rebuild.
- [ ] Fetch upstream `master`. If it has moved past 61a1a0e, cherry-pick onto the new tip, resolve any conflicts and rebuild.
- [ ] Use a dedicated branch for the PR (not the fork's working branch), so the PR #45 and UPSTREAM_PRS.md commits stay out.
- [ ] Decide whether to split commits 1-3 into their own PR.
- [ ] Decide whether the `Co-Authored-By` / `Claude-Session` trailers stay in the commit messages sent upstream.
- [ ] Do the local test with the owner present: arm with a door open (expect `command_result` with `code` 1 and the friendly text, and the Home Assistant error), arm and disarm normally (expect `success: true`), and watch `ready` follow the door. Update "Test evidence" and "Untested".
- [ ] Check the PR text, logs and screenshots for personal data: no names, addresses, panel or installer IDs, IP addresses, session IDs or access codes.

---

## E. Partition trouble flag for WebSocket clients, and a log of undecoded panel notifications

- **Proposed title:** `Keep the partition trouble flag current and send it to WebSocket clients`
- **Base branch:** `master`, after section D (it uses D's panel response code, 8 s bound and `capabilities`).
- **Commits to cherry-pick, in this order, onto a branch that already has sections A (first commit) and D:**
  1. `Read the partition trouble flag in the connect-time status pull`
  2. `Send the partition trouble flag to WebSocket clients`
  3. `Let SessionCommand callers log their own failures`
  4. `Track PanelCommandService commands in progress per session`
  5. `Re-read the partition trouble flag periodically`
  6. `Re-read the trouble flag right after an arm is rejected with code 1`
  7. `Log undecoded panel notifications once per command word`
  8. `End a trouble refresh round when the panel does not answer`
  9. `Rate-limit trouble read warnings per partition`
  10. `Log when the trouble refresh pauses for programming mode`
  11. `Do not dump undecoded payloads of access code commands`
  12. `Warn once per session while trouble reads keep failing`

  Commits 9-12 came out of review and build on 1-8 (12 replaces the per-partition warning state of 9). Do not include PR #45 or the UPSTREAM_PRS.md commits.
- **Dependency:** in a scratch clone, upstream `master` 61a1a0e plus section A's first commit, section D's eleven commits
  and these twelve cherry-pick with no conflicts. The solution builds with 14 warnings and 0 errors, the same warnings as
  61a1a0e (compared without line numbers), and the throwaway harness gives the same results there as on the fork.
- **TLink is touched:** `SessionCommand` (public record) gains `bool LogFailuresAtDebug` (default false; `SessionMediator`
  then logs its failure lines at Debug instead of Warning or Error, same templates). `ConnectionSettings` gains
  `TroubleRefreshIntervalSeconds` (default 60). `ITv2Session` gains the undecoded-notification log lines. Nothing in TLink
  is renamed or removed. Say this in the PR.
- **Independent parts:** commits 7 and 11 (log of undecoded notifications) stand on their own and could be a separate small PR.
- **Not in this PR:** no message class or handler for `Notification_Partition_Trouble_Status` (0x023F),
  `ModuleStatus_Trouble_Detail` (0x0817) or `ModuleStatus_Trouble_Detail_Notification` (0x0823), and no `trouble_types`:
  their payload formats are not established. Commit 7 only logs what the panel sends.

### PR body

````markdown
## Summary

WebSocket clients can now see whether the panel reports a trouble (for example AC power loss or a low battery) for a partition, and the flag stays current while the panel is connected:

- `PartitionState.TroublesPresent` comes from `ModulePartitionStatus` (`PartitionStatus2.TroublesPresent`, 0x02), which is already decoded and read by the status pull when a session connects.
- A new hosted service, `PartitionTroubleRefreshService`, re-reads `ModulePartitionStatus` for each partition of a connected session every `TroubleRefreshIntervalSeconds` seconds (a new per-connection setting, default 60, 0 = off) and updates only `TroublesPresent` from it.
- After an arm command is rejected with panel response code 1 (not ready to arm), the rejected partition is re-read 500 ms later in the background, so a client can tell "a trouble blocks arming" from "a zone is open" right away.
- `full_state` partitions and `partition_update` gain `"troubles": true|false`, and `full_state.capabilities` gains `"partition_troubles"`.
- Panel notifications that NeoHub cannot decode are logged at Information once per command word per session, and with their payload in hex at Debug.

## Motivation

- `TroublesPresent` was decoded but unused, and the status pull runs once per connection, so any flag taken from it would go stale (the same reason `triggered` is stale).
- A rejected arm with code 1 says only "not ready". With the trouble flag, a client can tell the user to check the keypad when no zone is open.
- Trouble notifications (`Notification_Partition_Trouble_Status` and the trouble detail messages) have no message classes, so they arrive as `DefaultMessage` and are dropped without a trace at the default log level. Nobody can see what the panel sends when a trouble occurs.

## Protocol additions

```json
{"type": "full_state", "sessions": [{"session_id": "<id>", "name": "<id>", "partitions": [{"partition_number": 1, "name": "Partition 1", "status": "disarmed", "ready": true, "troubles": true}], "zones": [...]}], "capabilities": ["command_result", "partition_ready", "partition_troubles"]}
{"type": "partition_update", "session_id": "<id>", "partition_number": 1, "status": "disarmed", "ready": true, "troubles": true}
```

- `troubles` is `PartitionState.TroublesPresent`. It does not depend on the partition status, and `status` values and the meaning of `ready` do not change.
- A change of `troubles` alone sends one `partition_update`. A re-read that finds no change sends nothing.

## Behaviour of the re-reads

- Setting: `TroubleRefreshIntervalSeconds` in each connection's settings, on the Connections page next to the other per-connection settings. A settings file without the field loads with 60. 0 turns the periodic re-read and the re-read after an arm rejection off; a change applies within about 5 s, without a restart.
- A session's worker starts once its connect-time configuration and status pull has finished (`SessionState.IsInitialized`); the first round runs one interval later. It stops when the session disconnects or the host shuts down; a reconnect gets a new worker after its own pull.
- Only `TroublesPresent` is written. `Status`, `IsReady`, the exit delay fields and `LastUpdated` are left alone, so this does not change live `triggered` / `pending` handling or anything clients see today. `UpdatePartition` is called only when the flag changed, and the refresh never creates a session or partition entry.
- One read at a time per session. A read is skipped while a `PanelCommandService` command (arm, disarm, bypass; a bypass counts as one operation from enter to exit) is in progress on that session, while the session's `ConfigLock` is held (configuration and user operations), and while the panel reports programming mode (`SessionState.IsInProgrammingMode`, which also covers installer writes from the web UI that do not take `ConfigLock`). The check runs before each partition. A programming-mode pause logs one Information line when it starts and one when it ends, so a flag that never clears is visible.
- Each read waits at most 8 s, as `PanelCommandService` does. When the panel does not answer, the rest of that round is skipped. A failed, refused or timed-out read, an answer for another partition and an answer without the second status byte keep the last value. Per session, the first failure while no partition is failing is a Warning, every further failure is Debug, and "Trouble status reads work again" is logged once every failing partition has been read successfully; so one partition that keeps failing gives one Warning, also while another one answers. `SessionMediator` logs these reads' failures at Debug.
- The re-read after a code-1 arm rejection is queued by an event (`IPanelCommandService.ArmRejectedNotReady`) that `ArmAsync` raises after the command stopped counting as in progress. The command's result (`command_result`, the old `error` reply, the UI snackbar) does not wait for it. It is dropped when that partition is being read at that moment, and a periodic read of the partition also serves it.

## Undecoded panel notifications

`ITv2Session` logs every `DefaultMessage` that is not the answer to a pending request, including sub-messages of a `MultipleMessagePacket` and the notifications discarded during the initial burst:

```
Undecoded panel notification Notification_Partition_Trouble_Status (0x023F), 3 payload bytes; later ones with this command word are logged at Debug only
Undecoded panel notification Notification_Partition_Trouble_Status (0x023F) payload: [01-00-01]
```

The payload is the panel's own. For command words of the access code and user code families and for `Connection_Request_Access`, whose payload may hold a code or key material, the Debug line gives only the length. Nothing is decoded or acted on.

## Changes

- TLink `ITv2/MediatR/SessionCommand.cs`, `SessionMediator.cs`: `LogFailuresAtDebug`.
- TLink `ITv2/ConnectionSettings.cs`: `TroubleRefreshIntervalSeconds` (`[Range(0, 86400)]`, default `DefaultTroubleRefreshIntervalSeconds` = 60).
- TLink `ITv2/ITv2Session.cs`: `LogUndecodedNotification(s)`, a per-session set of command words already logged, `MayCarrySecret`.
- `Services/Models/PartitionState.cs`: `TroublesPresent`.
- `Services/Handlers/PanelConfigurationHandler.cs`: the status pull sets `TroublesPresent`.
- `Services/IPanelCommandService.cs`, `PanelCommandService.cs`: `IsCommandInProgress(sessionId)`, `ArmRejectedNotReady` event and its args; `PanelAnswerTimeout` is `internal` so the refresh uses the same bound.
- `Services/PartitionTroubleRefreshService.cs` (new), registered with `AddHostedService` in `Program.cs`.
- `Services/ConnectionSettingsProvider.cs`: the factory-default trial copy keeps the interval.
- `Components/Pages/Connections.razor`: "Trouble Refresh Interval" field.
- `Api/WebSocket/Models/WebSocketMessage.cs`, `PanelWebSocketHandler.cs`: `Troubles` on `PartitionDto` and `PartitionUpdateMessage`, `partition_troubles` capability.

## Compatibility

- Additive. The replies to commands, with or without `request_id`, are unchanged byte for byte (other than section D's code-1 text). Clients that read `full_state` / `partition_update` strictly must accept the new `troubles` property.
- Clients should use `troubles` only when `full_state` lists `partition_troubles`.
- New traffic to the panel: one `ModulePartitionStatus` request per partition per interval (default 60 s), plus one after each arm rejected with code 1. These are the same requests the connect-time pull already sends.
- New log lines at Information: a trouble flag change ("Partition 1 troubles present" / "cleared"), the first undecoded notification of each command word per session, "Trouble status reads work again" after failures, and the start and end of a programming-mode pause.

## Test evidence

Offline only (no panel):

- `dotnet build NeoHub.sln` after every commit: 14 warnings and 0 errors, the same warnings as before (compared without line numbers).
- The throwaway harness from section D, extended. It runs the real `PanelConfigurationHandler`, `PartitionTroubleRefreshService`, `PanelCommandService`, `PanelStateService`, `PanelWebSocketHandler` and TLink `SessionMediator`, with a simulated panel behind a fake `IITv2Session`, and the real `ITv2Session` receive pump behind a fake transport:
  - All earlier checks keep their results, except the intended changes: the code-1 text, `troubles` in `full_state` / `partition_update` and the third capability. The replies without `request_id` (69 scenarios plus 5 new ones with a null session id or code 1 on partition 2) are byte-identical to the build before this PR.
  - The connect pull sets `TroublesPresent`. With the interval at 1 s and the pull held for 3 s, no extra read is sent during the pull; the first round follows one interval after it.
  - Unchanged reads send nothing; a troubles-only change sends exactly one `partition_update`; a read whose answer has other status, ready, delay and alarm bits changes only `TroublesPresent` (status, ready, exit delay fields, name and `LastUpdated` compared before and after). A live arm notification is not undone by later reads.
  - No read is sent while an arm waits 8 s for the panel or during a bypass (enter, write, exit). A read the panel never answers ends after 8.0 s, is the only request meanwhile, ends the round, and does not delay a disarm sent during it or an arm sent after it.
  - Transport failure, panel rejection, NACK, an answer for the other partition, a short answer and an exception keep the last value and give exactly one Warning for both partitions together (all categories counted) until the reads work again, and one more after that; `SessionMediator` lines are Debug. With one partition failing every round and the other answering: one Warning, no recovery line in between, one recovery line at the end.
  - No read while the session's `ConfigLock` is held or while the panel reports programming mode (one Information line when the pause starts, one when it ends; a `ConfigLock` skip during the pause does not end it); reads resume afterwards.
  - Interval 0: no reads and no re-read after a code-1 rejection; changing it to 1 at runtime started the reads (first read 2.0 s after the change). A settings file without the field loads 60; 0 and 15 load as written.
  - Disconnect stops the reads; a reconnect starts a new worker after the new pull; `StopAsync` stops everything.
  - Code-1 arm: the `command_result` (or the old `error`) goes out at once; the single re-read follows 0.50 s later, then the `partition_update`. A re-read that hangs does not delay the reply. Disarm code 1, arm code 17 and a code-1 rejection for a partition with no state trigger no re-read. A re-read requested while that partition is being read is dropped; one requested while another partition is being read runs once, 0.5 s after that read.
  - Never two reads at once on a session, and no read reached the panel while a command was outstanding there.
  - Undecoded notifications: Information once per command word per session (pre-ready burst, normal notifications and `MultipleMessagePacket` sub-messages), Debug with the hex payload every time, nothing for typed messages or for a `DefaultMessage` that answers a pending request. `Notification_Access_Code_Length` with a dummy BCD code: length only, no payload, in these lines.
  - No dummy access code appears in any log line or frame.
- A real NeoHub process (no panel), started with a settings file without the new field: it starts, `full_state` lists the three capabilities, the Connections page shows the interval as 60, and it shuts down cleanly.

Untested:

- Everything on a real panel: whether `PartitionStatus2.TroublesPresent` follows a real trouble (AC loss, low battery) and clears again, whether code 1 comes back for an arm blocked by a trouble, and whether the panel sends 0x023F / 0x0823 at all.
- The effect of a status request every 60 s on the panel and the communicator over days.

## Known limitations (not changed here)

- TLink `CommandRequestReceiver` matches a `ModulePartitionStatus` answer by type, not by partition, and a cancelled receiver stays registered until the next cleanup. If the panel answers the next request without acknowledging it first, the stale receiver takes that answer and the next read times out (seen with the receivers called through reflection; with an acknowledgement first, the new read gets its answer). The refresh checks the partition in the answer and ends the round after a timeout.
- `ITv2Session._pendingReceivers` is a plain `List` that sends add to while the receive pump enumerates it; the re-reads add a few more sends to that pattern.
- Commands from the Dev Console and other `SessionCommand` callers outside `PanelCommandService` are not tracked as in progress; they are only avoided while they hold `ConfigLock` or the panel reports programming mode.
- TLink's existing Debug line `RX detail: {Message}` still writes every property of each inbound message, including the payload of an undecoded message of the access code family (seen with a dummy code in the harness). It is not written at the default Information level.
- An immediate re-read that finds a command in progress when it fires is skipped, not retried; the next periodic round reads the partition.
````

### Checklist for the owner before submitting

- [ ] The owner has given an explicit OK to open this PR.
- [ ] Sections A and D are merged upstream, or submitted first.
- [ ] Fetch upstream `master`. If it has moved past 61a1a0e, cherry-pick onto the new tip, resolve any conflicts and rebuild.
- [ ] Use a dedicated branch for the PR (not the fork's working branch), so the PR #45 and UPSTREAM_PRS.md commits stay out.
- [ ] Decide whether commits 7 and 11 go in their own PR, and whether to squash commits 9, 10 and 12 into 5, and 11 into 7.
- [ ] Decide whether the `Co-Authored-By` / `Claude-Session` trailers stay in the commit messages sent upstream.
- [ ] Do the local test with the owner present: unplug the panel's AC power (or wait for a real trouble) and watch `troubles` turn true within the interval and false after it clears; arm during the trouble with no zone open and note the response code; check the NeoHub log for "Undecoded panel notification" lines. Update "Test evidence" and "Untested".
- [ ] Check the PR text, logs and screenshots for personal data: no names, addresses, panel or installer IDs, IP addresses, session IDs or access codes.
