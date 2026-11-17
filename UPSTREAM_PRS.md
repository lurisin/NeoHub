# Upstream PR drafts for BrianHumlicek/NeoHub

> **Drafts only.** Nothing in this file has been submitted upstream. Nothing may be
> submitted (no PR, issue or comment) without the owner's explicit OK.

This fork's `toronto` branch is upstream `master` (61a1a0e) plus these commits:

| Commit subject | Upstream status |
|---|---|
| `Remove the exact disconnected WebSocket client` | Upstream PR #45, carried unchanged (section B) |
| `Add arm_home_instant / arm_away_instant WebSocket commands` | Draft PR (section A) |
| `Add "Stay Instant" arm button to the home page` | Optional second commit of the same draft PR (section A) |
| `Add UPSTREAM_PRS.md with upstream PR drafts` | Fork only, never submit |

Section C records why panic / fire / auxiliary activation (`ModuleControl_Activate_FAP`, 0x0908) is not proposed.

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
