# Creature handoff improvements: are they worth building?

Data: Praetoris/Warp monitoring, 6-11 October 2026. Analysis date: 14 October 2026.

## In short

- **Q1 Target adoption: NO-GO.** When the old target is a player within 30 m, the new owner takes over 2 s to find a target, or never finds one, in 28.9% of handoffs. That clears the 15% bar. But it happens only 0.34 times per player-hour, and the bar is 0.5.
- **Q2 No mid-swing handoffs: NO-GO.** Only 6.1% of the handoffs NPS could defer happen during an attack or stagger (bar: 10%). Those handoffs also go no worse than the others: 23.7% have a bad follow-up, against 47.2% for the rest.
- **Q3 Prefer the attacked player: UNCLEAR.** The pre-registered rule says NO-GO, by one poll interval. The owner is not the targeted player for 36.8% of shared-fight time, at a mean extra delay of 74 ms; both pass. But a creature keeps one target for a median of 9.1 s, and the bar is 10 s (twice the server's 5 s Min Hold).

## Data and coverage

| Folder | NPS | Hours | Player-hours | Handoffs |
|---|---|---|---|---|
| 20261006 | 1.14.0 | 23.8 | 522.8 | 164,203 |
| 20261007 | 1.15.1 | 23.8 | 506.4 | 156,801 |
| 20261008 | 1.16.0 | 23.8 | 437.2 | 158,403 |
| 20261009 | 1.16.0 | 23.8 | 514.9 | 194,658 |
| 20261010 | 1.16.0 | 23.8 | 578.9 | 180,594 |
| 20261011 | 1.16.0 | 19.0 | 521.5 | 176,120 |
| **Pooled** | | | **3,081.7** | **1,030,779** |

- **Versions and settings.** I read versions from each folder's session record. The settings were the same every day: Min Hold Seconds 5 (the code default is 2), Creature Proximity Radius 48, Challenge Margin 25. No setting changed mid-session.
- **The 1.11.1 day (20261001, 11.9 h, 308 player-hours)** has the same record fields, but ran an older arbiter, so it is a check only, not in the pooled verdicts.
- **Handoffs by cause.** Rescue 541,157; Release 387,293; Proximity 49,424; Gameplay 38,323; Optimise 6,485; Capacity 6,330; Remote 1,259; Leader 413; Upload 95.
- **Handoffs with the alert or target flag set.** Release 48,850; Rescue 48,806; Proximity 11,610; Gameplay 4,960; Optimise 1,597; Capacity 812; others 55.
- **Who uploads.** Every player uploads: 1,433 of 1,446 sessions sent batches.
- **Gaining-side join.** The new owner's `c_owner` record joins on (zdo, rev) for 87.9% of handoffs to a player, and 88.1% of the alert/target ones.
- **Losing-side join.** The old owner's `c_owner` record joins for only 32% (35% of the alert/target ones). An owner who has walked out of range never receives the change, which covers Release and most Proximity moves.
- **Host-owned creatures are not covered.** The dedicated host gains 6.5% of creatures but writes no `c_target` or `c_reacq`, because it has no creature instances. Those creatures are invisible to Q1 and Q3.
- **File quality.** One truncated line, no client lines outside a batch, no gzip errors.

## Method

- **Joins.** The host's `handoff` and the clients' `c_owner` records join exactly on (zdo, rev). In 0.9% of fight records the host's `from` disagreed with the uploader that recorded the loss.
- **Clock alignment.**
  - **Offsets:** offset = batch.t − clientMs − rtt/2. I took the 10th percentile per 10-minute window and interpolated between windows.
  - **Validation:** checked against 564,699 handoffs where the host's time and the gaining client's flip time are both known. Per day, the host-to-client delay is p10 112–127 ms, p50 209–237 ms and p90 394–436 ms. None came out earlier than −50 ms.
  - **Drift:** median 48–61 ms per hour, which the 10-minute windows absorb.
  - **Flagged sources:** 203 of 1,178 sources (17%) have an anchor spread above 250 ms. The spread includes real send-queue delay, so it overstates the alignment error. Leaving those owners out changes the Q3 mismatch from 36.8% to 38.1%.
- **Position approximation (Q3).**
  - Q3 uses the target player's interpolated position (peer records every 1–4 s; gaps over 10 s are unknown) as the creature's position.
  - **Check:** at 95,443 deaths of creatures with a live player target, the death point was a median 3.5 m from that player; 97% within 30 m, 99.4% within 48 m. At handoff instants it is a median 38 m (the old target is often the owner who walked away), so it holds for fights, not handoffs.
- **Pre-registered thresholds.** These were written to `prereg.txt` before any result existed.
  - **Q1 GO:** bad share ≥ 15% of adoptable handoffs, and ≥ 0.5 per player-hour after scaling by gaining-side coverage. Bad means a timeout, or a target found after more than 2 s.
  - **Q2 GO:** mid-swing share ≥ 10% in the deferrable pool (Optimise, Proximity, Capacity, Upload), and a composite bad follow-up ≥ 1.5× with non-overlapping Wilson 95% CIs.
  - **Q2 bad follow-up:** any one of an unapplied hit at the old owner within 5 s, a slow or failed reacquire, a drag-back, or a viewer teleport within 3 s.
  - **Q3 GO:** mismatch ≥ 30% of shared-fight time, mean penalty ≥ 40 ms, and Kaplan-Meier median dwell ≥ 2 × Min Hold, which is 10 s at this server's value of 5 s. A dwell is time on one target, ended by any target change. Shared fight means 2+ players within 48 m; penalty = (rtt(owner) + rtt(target)) / 2.

## Q1 Target adoption

- **Population.** 51,826 alert/target gains have a joined `c_reacq`. The old target is known for 26.6% of them.
- **Gains from "no owner" are not adoptable.** 69% of gains arrive from no owner (a Rescue after a Release). For 5,198 of those I could find the releasing owner's last target, and that player was within 30 m at the rescue in only 3 cases. So scaling the count up by the "old target known" share (1.28 per hour) would overstate it.
- **Adoptable:** 3,209 handoffs.

| Cause | Adoptable | Timeout | Found > 2 s (of ok) | Bad [95% CI] | Found, p50 |
|---|---|---|---|---|---|
| Proximity | 2,324 | 22.4% | 13.1% | 32.2% [30.3–34.1] | 983 ms |
| Optimise | 582 | 4.0% | 9.2% | 12.4% [9.9–15.3] | 789 ms |
| Capacity | 177 | 19.2% | 21.1% | 36.2% [29.4–43.5] | 1,035 ms |
| Rescue | 115 | 19.1% | 20.7% | 35.7% [27.5–44.7] | 1,131 ms |
| **All** | **3,209** | **18.7%** | **12.9%** | **28.9% [27.3–30.5]** | **954 ms** (p90 2,445, p99 8,232) |

- **Rate.** 926 bad events in 3,081.7 player-hours is 0.30 per player-hour. Divided by the 0.881 gaining-side coverage, that is **0.34**.
  - **By day:** 0.18–0.41 on 1.14.0–1.16.0, and 0.46 on the 1.11.1 day.
  - **Upper estimate:** if the 2,169 gains with an unknown old target are adoptable as often as the known ones, the rate is about 0.40.
- **When the reacquire succeeds, it is usually the same player.** 86% of found targets are the old target, so adoption would mostly save time (about 1 s at p50).
- **The new owner is usually the target.** In 82% of adoptable handoffs the new owner is the player the creature was attacking. It still times out 21% of the time; the commonest prefabs here are Greydwarf, Neck, Leech, Charred, Goblin and Boar. The problem is real but rare: about once per player per three hours.

## Q2 Mid-swing handoffs

- **Population.** 14,503 old-owner records for fighting creatures, all joined to a host handoff.

| Cause | Fight handoffs | Mid-swing | Share [95% CI] |
|---|---|---|---|
| Optimise | 1,578 | 169 | 10.7% [9.3–12.3] |
| Proximity | 11,641 | 667 | 5.7% [5.3–6.2] |
| Capacity | 821 | 24 | 2.9% [2.0–4.3] |
| Upload | 17 | 1 | 5.9% |
| **Deferrable pool** | **14,057** | **861** | **6.1% [5.7–6.5]** |
| Rescue / Release / Gameplay (not deferrable) | 423 | 5 | 1.2% [0.5–2.7] |

Of the 861 mid-swing handoffs, 816 were attacking and 45 were staggering.

| Deferrable pool | Mid-swing | Not mid-swing |
|---|---|---|
| Composite bad follow-up | 23.7% [21.0–26.7] | 47.2% [46.4–48.1] |
| Reacquire timeout or > 2 s | 20.8% | 51.7% |
| Unapplied hit at old owner, 5 s | 0 / 861 | 0 / 13,196 |
| Drag-back | 0 | 0 |
| Viewer teleport (≥ 5 m) within 3 s | 4.2% | 5.9% |
| Creature died within 10 s | 24.0% | 5.4% |
| Moved again by NPS within 10 s | 5.2% | 6.2% |

- **Ratio.** Mid-swing handoffs have bad follow-ups at 0.50× the rate of the others. That is mostly because the not-mid-swing group includes alert creatures with nobody to attack, which time out.
- **Rate.** Mid-swing deferrable handoffs come to 0.28 per player-hour.
- **The unapplied-hit measure is empty.** The hit router works: only 1,005 of 1.63 M hits all week reached a machine that did not own the creature.

## Q3 Owner ≠ target in shared fights

| Measure | Value |
|---|---|
| Creature time with a player target | 11.58 M creature-seconds |
| Of which shared fight | 1.383 M (11.9%); the arbiter's own contested share is 15.4%, which is consistent |
| **Mismatch share** | **36.8%** (by day 33.0–44.2%; first 30 s of an episode 44.2%) |
| **Penalty in mismatched seconds** | **mean 74.4 ms**, p50 59 ms, p90 136 ms |
| Penalty averaged over all shared-fight time | 27 ms |
| Owner within 48 m of the target when mismatched | 98.6% |
| Mismatched seconds where the target's ping is not > 50 ms worse | 77.8% |
| **Median time on one target (Kaplan-Meier)** | **9.1 s** (by day 9.0–10.1 s) |
| Completed dwells shorter than 5 s / 2 s | 42% / 3.5% |
| Median time until it turns to a *different* player | 16.1 s (by day 13.1–20.4 s) |
| Switches to a different player | 1.56 per shared creature-minute |

- **Against the rule.** Mismatch passes, penalty passes, and dwell misses by about one 1-second poll.
- **Why I call it UNCLEAR.** The verdict turns on choices the data cannot settle:
  - With the code default Min Hold of 2 s, the dwell test would pass.
  - It would also pass if only a turn to a different player ended a dwell (median 16.1 s), rather than any change including a dropped target.
- **The penalty is a lower bound.** The measured delay from host decision to client flip is about 210 ms at the median, so real swing-to-dodge delay is longer than the RTT halves.

## Caveats

- **Losing-side coverage is low (35%),** so most old targets are unknown. The "no owner" result rests on 5,198 checkable cases.
- **Host-owned creatures are not covered** (6.5% of gains).
- **`c_target` is only written on change, polled once a second.** A creature that unloads without a record keeps its episode open until the 120 s cap, which ended 12% of episodes. The first-30-s sensitivity has a higher mismatch, not a lower one.
- **Q2's composite is dominated by reacquire timeouts,** which depend on fight state, not only on the handoff.
- **One server, one mod pack,** and every client ran NPS.
- **The 1.11.1 day points the same way:** Q1 bad share 28.6% at 0.46 per hour, Q2 mid-swing 4.0%, Q3 mismatch 40.9%, penalty 110 ms, dwell 10.1 s.

## What a GO would take

Nothing passed its pre-registered rule. Q3 came closest, so here is a short sketch for it.

- **Settle the question first.** Record target changes at 0.25 s in the next monitoring run. Pre-register "time until a different player" as the dwell measure, and decide whether the hold should be 2 s or 5 s.
- **Q3 needs Q1's mechanism.** Following the target means about 12 extra owner changes per player-hour: 1.56 switches per shared creature-minute times 7.5 shared creature-minutes per player-hour.
  - **The cost without adoption:** each of those starts the creature's AI cold. Today about 31% of handoffs to the attacked player end up slow or with no target, which projects to roughly 3–4 bad reacquires per player-hour, ten times Q1's current rate.
  - **What to build:** an owner-written target id on the ZDO, adopted by the new owner. Prefer the attacked player only when 2+ players are within 48 m and rtt(target) ≤ rtt(owner) + 50 ms. Hold for at least Min Hold. Never move a creature mid-swing.

## Reproducing

- **Script:** `C:\Users\carls\AppData\Local\Temp\claude\C--Users-carls-Documents-projects-Valheim-Stuff-Valheim-Network-Performance-System\94e73872-c69e-472c-9ada-05325f575df4\scratchpad\handoff-value\nps_handoff_value.py` (stdlib only). Pre-registration is `prereg.txt` in the same folder.
- **Run it:** `python -I nps_handoff_value.py scan <folder> out/<name>`, then `align out/<name>...`, then `analyze out <names...>`, then `report out`.
- **Note:** the scratchpad is temporary. Copy the script elsewhere if you want to keep it.
