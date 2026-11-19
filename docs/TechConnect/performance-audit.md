# TechConnect — Comprehensive Performance Audit

| Field | Value |
|---|---|
| Date | 2026-11-19 |
| Repository | Standalone TechConnect (`C:\Users\chrisk\source\repos\TechConnect`) |
| Scope | TechConnect.Protocol, TechConnect.Client, TechConnect.Server |
| Phase | Consolidation / release-readiness review (remediations already in working tree) |
| Auditor | Automated code review + isolated micro-measurements + suite qualification |

---

## A. Executive summary

TechConnect’s production data path is **correctness- and isolation-oriented**: bounded operator queues with detach-on-overflow, drop-oldest remote outbound buffering, frame-size limits before allocation, and mTLS retained end-to-end. There is **no dedicated BenchmarkDotNet (or equivalent) suite** in-repo; existing tests validate behaviour, not sustained throughput or allocation budgets.

Isolated Release micro-measurements on this workstation show that **frame and serial paths are CPU-cheap relative to allocation rate**. The dominant structural costs on hot paths are:

1. ~~**Terminal paint string concatenation / GDI churn / full-viewport CPU** (PERF-UI-01)~~ — **alloc closed + dirty-row CPU remediated** (2026-11-19): ~15 KB/paint Gen0 0; in-place/cursor paints ~0.2 ms vs ~3.7 ms full; scroll/`YDisp` still full repaint.
2. ~~**Server device→operator double copy** (PERF-SRV-01)~~ — **remediated** (2026-11-19): `BoundedByteQueue.TryEnqueueOwned` takes `FrameReader` payload ownership; isolate alloc ~2× → ~1× payload; defensive `ToArray` retained only for non-owned enqueue and runs **after** capacity check.
3. ~~**Client serial RX ownership copy** (PERF-CLI-01)~~ — **partially remediated** (2026-11-19): local-console-only uses ephemeral read-buffer view (0 durable alloc/op in isolate); remote-ingress path still allocates one durable payload (required).
4. ~~**SerialConsoleBuffer** per-byte enqueue + front `RemoveRange` (PERF-CLI-01/02 buffer path)~~ — **remediated** (2026-11-19): fixed-capacity ring with bulk `Span` copy; overflow O(1); overflow isolate ~100× ops/s vs `List`/`RemoveRange`.
5. ~~**Unbounded `Incoming` Channel** when remote session is not configured~~ — **PERF-CLI-03 confirmed and remediated** (2026-11-19).

No **Critical** severity defects were measured that would prevent serial-console workloads at typical baud rates on a modern workstation. Highest remaining priorities: optional Client ArrayPool for remote-durable RX (deferred); Protocol **PERF-PROTO-02** frame-writer flush/coalesce (hypothesis; distinct from remediated PERF-SRV-03).

Security/reliability constraints (TLS/mTLS, frame limits, bounded operator queues, no payload logging) must be preserved in any subsequent remediation.

---

## B. Existing baseline

### B.1 Inventory of in-repo performance measurements

| Asset | Present? | Notes |
|---|---|---|
| BenchmarkDotNet / dedicated bench projects | **No** | No `*Benchmark*` projects or packages found |
| Allocation / throughput CI gates | **No** | |
| Functional tests exercising hot paths | **Yes** | Protocol framing, serial pipeline, forwarding, overflow detach |
| Production `Stopwatch` usage | Diagnostic only | Auth latency, copy transfer, traceroute, serial drain waits — not workload baselines |

**Conclusion:** Existing tests are **workload models**, not performance baselines. They do not report throughput, latency histograms, or allocation rates.

### B.2 Environment (this audit)

| Item | Value |
|---|---|
| OS | Windows 10.0.26200 (win-x64) |
| CPU | 12th Gen Intel Core i9-12900KF (16 cores / 24 logical) |
| RAM | ~32 GB |
| .NET SDK | 10.0.404 (`global.json`) |
| Runtime used for probes | .NET 10.0.15, Workstation GC, Interactive latency |
| Project TFMs | Protocol/Server `net10.0`; Client `net10.0-windows` |
| Build config for measurements | **Release** |

### B.3 Functional tests executed (not performance scores)

| Command | Result |
|---|---|
| `dotnet test tests/TechConnect.Protocol.Tests -c Release` | **29 passed**, 0 failed (~93 ms test duration) |
| `dotnet test tests/TechConnect.Client.Tests -c Release --filter "…SerialConsoleBuffer\|…SerialRxPipeline\|…SerialRemote"` | **31 passed** |
| `dotnet test tests/TechConnect.Server.Tests -c Release --filter "…SerialForwarding\|…BoundedByteQueue"` | **8 passed** |

These confirm behavioural contracts used as workload models; durations are **not** application performance metrics.

### B.4 Isolated micro-measurements executed

A temporary probe outside the repository (`%TEMP%\TechConnectPerfAudit\`) referenced `TechConnect.Protocol` and re-implemented Client/Server allocation patterns for measurement. **No production source was modified.**

#### FrameReader / FrameWriter (MemoryStream, no TLS)

| Workload | Ops | ops/s | alloc/op | Gen0 Δ |
|---|---:|---:|---:|---:|
| FrameReader-only, 64 B payload | 50 000 | ~1.92M | **112 B** | 0 |
| FrameReader-only, 1024 B | 50 000 | ~1.61M | **1072 B** | 3 |
| FrameReader-only, 4096 B | 25 000 | ~1.07M | **4144 B** | 6 |
| Writer+Reader roundtrip, 64 B | 50 000 | ~4.23M | **112 B** | 0 |
| Writer+Reader roundtrip, 1024 B | 50 000 | ~5.01M | **1072 B** | 3 |
| Writer+Reader roundtrip, 4096 B | 25 000 | ~2.25M | **4144 B** | 6 |

**Interpretation:** Allocation ≈ `payloadLength + ~48 B` per read (array + `ProtocolFrame` / async machinery). CPU throughput on MemoryStream is far above serial baud ceilings; **allocation rate**, not parse CPU, is the primary framing concern.

#### Client serial RX ownership copy (`new byte[read]` + `BlockCopy`)

| readSize | Ops | ops/s | alloc/op |
|---|---:|---:|---:|
| 64 B | 50 000 | ~9.5M | **88 B** |
| 1024 B | 50 000 | ~15.0M | **1048 B** |
| 4096 B | 25 000 | ~3.7M | **4120 B** |

#### Server RX double-copy (FrameReader-style alloc + `ToArray`)

| payload | Ops | ops/s | alloc/op |
|---|---:|---:|---:|
| 64 B | 50 000 | ~8.6M | **176 B** (~2×) |
| 1024 B | 50 000 | ~7.1M | **2096 B** |
| 4096 B | 25 000 | ~1.8M | **8240 B** |

#### SerialConsoleBuffer-style algorithms

| Scenario | Ops | ops/s | Notes |
|---|---:|---:|---|
| Enqueue 64 B + drain every 8 chunks, 1 MiB cap | 50 000 | ~5.3M | No overflow |
| Enqueue 1024 B + drain every 8, 1 MiB cap | 10 000 | ~0.85M | No overflow |
| **Overflow, no drain**, 1024 B chunks, 64 KiB cap | 20 000 | **~0.30M** | `RemoveRange` path; ~20 MB dropped |

#### Paint-style string concatenation vs StringBuilder (PERF-02 isolate)

Simulated `runText += ch` for a **40×120** viewport × **200** full paints (string path only; no GDI):

| Method | Total time | Alloc / frame | Gen0 |
|---|---:|---:|---:|
| `string +=` per cell (mirrors `PaintLine`) | 15.0 ms | **~700 800 B** | 8 |
| `StringBuilder` reuse | 0.97 ms | **~3 B** | 0 |

At the Client’s **75 ms** console flush interval, a sustained full invalidate could approach **~9 MB/s** of string allocation from concatenation alone before brushes/pens/`TextRenderer`.

### B.5 Limitations of these measurements

- MemoryStream / in-process copies — **not** TcpClient, SslStream, serial drivers, or WinForms message pump.
- No multi-connector or multi-SSH sustained load.
- No physical serial hardware or live Server↔Client deployment.
- Paint bench isolates string growth; real `OnPaint` also allocates GDI objects and draws text.
- SerialConsoleBuffer probe mirrors algorithms but is not the WinForms-locked production type under UI-thread contention.
- Roundtrip ops/s should not be compared naïvely to reader-only (different stream residency / GC pressure shapes); **alloc/op** is the stable signal.

---

## C. Architecture and hot-path analysis

### C.1 End-to-end data paths

```
NOC SSH ──► TechConnectCliSession.ForwardOperatorInputAsync (1 KiB buffer)
         ──► ProtocolSession / FrameWriter (header + payload + FlushAsync)
         ──► SslStream / TCP ──► Client TcpMtlsRemoteConsoleTransport
         ──► FrameReader ──► HandleRemoteSerialDataAsync (ToArray)
         ──► SerialConnectionService.WriteAsync ──► serial device

Device RX ──► SerialConnectionService.ReadLoopAsync (4 KiB scratch → owned copy)
           ├─► Channel Incoming ──► BridgeSerialRx ──► RemoteOutboundBuffer (≤4 MiB, drop-oldest)
           │                      ──► SendSerialDataAsync ──► FrameWriter ──► Server
           └─► DataTransferred ──► SerialConsoleBuffer (≤1 MiB display)
                                  ──► 75 ms UI drain ──► VtTerminalHost / XTerm.NET
                                  ──► SerialTerminalControl.Invalidate / OnPaint

Server RX ──► FrameReader ──► HandleSerialData
           ──► OperatorAttachmentManager.TryEnqueueDeviceRx
           ──► BoundedByteQueue (≤256 KiB, refuse → detach operator)
           ──► PumpOperatorOutboundAsync ──► SSH WriteRawAsync (gated WriteAsync; Flush removed — PERF-SRV-03)
```

### C.2 Likely bottlenecks by layer

| Layer | Likely bottleneck | Nature |
|---|---|---|
| Client UI | Scroll/`YDisp` full repaint; residual `TextRenderer` strings (PERF-UI-01 closed for alloc + dirty-row CPU) | Latency / UI thread CPU |
| Client serial RX | Remote-durable payload alloc (PERF-CLI-01 partial); local ephemeral done; Incoming gated (PERF-CLI-03) | Allocation |
| Protocol framing | Per-frame payload `byte[]`; per-frame `FlushAsync` (PERF-PROTO-01/02 open) | Allocation / syscall amplification under TLS |
| Server attachment | FrameReader payload remains; activity touch lock removed (PERF-SRV-01/02) | Allocation residual; contention remediated |
| Control plane | Source-gen JSON + UTF-8 string bridge on Client | Low rate — secondary |

### C.3 What is already good

- Frame limits enforced **before** payload allocation (`FrameReader` / `FrameWriter`).
- Header buffers reused (6 bytes) on reader and writer.
- Control JSON uses **source-generated** `ProtocolJsonContext` (AOT-friendly, no reflection on hot control types).
- TCP `NoDelay = true` on Client connect and Server accept.
- Client console path batches UI work on a **75 ms** timer (no per-byte `Invoke`).
- Server slow-operator policy **detaches** the attachment without stalling the connector reader (tested).
- Client remote outbound buffer is **bounded** (default 4 MiB) with drop-oldest; serial reader is not blocked.
- Session isolation: one operator attachment per connector; no serial broadcast fan-out.

---

## D. Measured findings

Severity: Critical / High / Medium / Low / Informational.  
Evidence classes: **Measured** (probe numbers), **Code observation** (source), **Hypothesis** (unverified runtime impact).

### PERF-UI-01 — Full viewport repaint with per-cell string concatenation

| Field | Detail |
|---|---|
| Finding ID | PERF-UI-01 (maps to prior PERF-02) |
| Component | `TechConnect.Client` — `SerialTerminalControl.OnPaint` / `PaintLine` |
| Source | `src/TechConnect.Client/Forms/SerialTerminalControl.cs` |
| Evidence | See original isolate + WinForms baseline below |
| Impact | UI-thread Gen0 under sustained serial output |
| Severity | **High** (pre-fix); allocation pressure **remediated** |
| Confidence | **High** |
| **Disposition** | **Fully qualified (alloc + dirty-row CPU + correctness)** — see § PERF-UI-01 disposition |

### PERF-UI-01 disposition (baseline + allocation remediation + dirty-row CPU + correctness)

**Verdict (2026-11-19): Allocation/Gen0 closed; dirty-row CPU remediated; paint/update lifecycle and painted-version invariant corrected and regression-tested. In-place/cursor ~0.2 ms vs ~3.6–4.4 ms full paint. Nested-Write lost-update and false painted-version (v10 drawn / v11 fingerprint) fixed. Scroll/`YDisp` still full-repaints. Not an end-to-end UI-responsiveness claim without physical serial soak.**

#### Confirmed bottlenecks (pre-fix)

| Bottleneck | Location | Evidence |
|---|---|---|
| Per-cell `runText += ch` | `PaintLine` | Original isolate ~701 KB/frame; WinForms baseline ~1.0 MB alloc/paint |
| `new SolidBrush` / `new Pen` per run | `PaintLine` / cursor | Baseline Gen0 during 80 paints |
| Full viewport redraw | `OnPaint` | Still used for scroll/`YDisp` change, resize, clear, reset; incremental updates use dirty rows |

Batching already present: `MainForm` drains `SerialConsoleBuffer` every 75 ms → one `Write` → dirty or full `Invalidate` (no per-byte UI dispatch).

#### Changes implemented (allocation pass)

- Reuse `StringBuilder` for attribute runs (one `ToString()` per run for `TextRenderer`, not per cell).
- Cache `SolidBrush` / `Pen` by ARGB; dispose caches with the control; soft cap `MaxCachedGdiObjects = 384` (flush-and-rebuild) so truecolor SGR cannot grow GDI handles without bound.
- Cache bold font; dispose on font change and control dispose; avoid allocating `" "` for empty cells (`Append(' ')`).
- `_isDisposing` gate on `Write` / paint / wheel so pending UI work is ignored after dispose begins.

#### Changes implemented (CPU / repaint pass)

- XTerm.NET 3.0.3 exposes `BufferLine.Version` (and events such as `Scrolled` / `ViewportRedrawStarting`) but **no public dirty-region API**. No library fork.
- After each `Write`, compare viewport line versions + cursor to the last paint fingerprint; invalidate only dirty row strips (or full when `YDisp`/size changes or ≥50% rows dirty).
- `OnPaint` paints from an explicit pending-row mask (double-buffering expands `ClipRectangle` to the full client, so clip alone is insufficient).
- Partial row paints clear the row strip before `PaintLine` so default-background cells do not leave stale glyphs.

#### Dirty-row correctness qualification (2026-11-19)

**Synchronization model:** `Write` / `OnPaint` / wheel / resize run on the WinForms UI thread (`MainForm` 75 ms drain does not call into the control from RX threads). Nested message pumps during GDI/`TextRenderer` are still possible, so the paint path must tolerate re-entrant `Write`.

**Lifecycle guarantee (after correctness fixes):**

1. `InvalidateDirtyRows` ORs dirty rows into `_pendingPaintRows` (and/or sets `_pendingPaintFull`) then `Invalidate`s.
2. `OnPaint` **snapshots** the paint plan, then **clears pending immediately**.
3. For each painted row, capture `BufferLine.Version` **before** `PaintLine`, render, then if live version advanced keep the row pending.
4. Fingerprints store the **pre-paint** versions only (never a newer live version) for rows this pass rendered.
5. A nested `Write` after step 2 can re-dirty rows / schedule full paint without being cleared by this paint’s completion.
6. Cursor-only moves dirty old and new cursor rows without requiring `BufferLine.Version` changes (`CursorBlink` is disabled in `VtTerminalHost`).

**Painted-version invariant:** a row’s recorded painted version must never advance beyond the version whose contents were rendered. Regression: `NestedSameRowUpdate_AfterPaintLine_DoesNotAdvancePaintedVersionPastRendered`.

**Defects found and fixed:**

1. Pending mask cleared *after* fingerprinting all rows → nested dirty bit lost (**lost update**). Fixed earlier: snapshot→clear-pending→paint→fingerprint painted rows only.
2. `CapturePaintedState` re-read live `Version` after paint → could record v11 while v10 was drawn (**false painted version**). Fixed: fingerprint pre-paint version; re-dirty if version changed during/after `PaintLine`.

**Full invalidation:** `YDisp`/cols/rows mismatch, ≥50% dirty, clear, mouse-wheel scroll, font change, grid resize, overflow `Reset` path, missing prior fingerprint.

**Disposal:** `_isDisposing` gates `Write`/`OnPaint`; dispose clears fingerprints/pending and GDI caches (384 cap unchanged).

#### Probe integrity

`MeasurePaintAndWrite_RecordsMetrics` drives a live `SerialTerminalControl` on an STA WinForms form, calls `control.Refresh()` under `GC.GetTotalAllocatedBytes(precise: true)`, and therefore measures the real `OnPaint` → `PaintLine` → `TextRenderer.DrawText` path (not an isolated string-building microbench). Microbenchmark concat figures from the original audit remain contextual only and are not mixed into these WinForms numbers.

#### WinForms before / first-after (historical single runs)

Command: `dotnet test … --filter MeasurePaintAndWrite_RecordsMetrics`  
Environment: Windows 10.0.26200, .NET 10.0.15, Workstation GC.  
**Limitation:** the pre-optimisation binary is no longer executable in-tree; “Before” values are the recorded baseline from the remediation session (not re-measured against a reconstructed fork).

| Workload | Metric | Before (recorded) | First after (recorded) |
|---|---|---:|---:|
| ascii | alloc/paint | 1 000 778 | 15 774 (~63×) |
| ascii | avg paint ms | 3.529 | 4.142 |
| ascii | p95 paint ms | 4.047 | 5.534 |
| ascii | Gen0 / 80 paints | 5 | **0** |
| sgr | alloc/paint | 854 440 | 18 014 |
| cursor | alloc/paint | 999 389 | 15 774 |
| utf8 | alloc/paint | 1 009 759 | 16 545 |
| sustained 40 writes+paints | total alloc | 39 977 728 | 574 536 (~70×) |
| sustained | Gen0 | 2 | **0** |

#### Final qualification — repeatability (optimised path, 5 Release STA runs)

Same methodology: ~120×40 viewport, 80 paints/workload, Windows 10.0.26200, .NET 10.0.15. Individual runs (alloc_per_paint / avg_ms / p95_ms / gen0):

| Run | ascii alloc | ascii avg_ms | ascii p95 | sgr alloc | cursor alloc | utf8 alloc | sustained alloc | Gen0 |
|---:|---:|---:|---:|---:|---:|---:|---:|---:|
| 1 | 15 089 | 3.830 | 5.128 | 15 221 | 14 197 | 24 620 | 570 048 | 0 |
| 2 | 15 245 | 3.458 | 4.056 | 15 168 | 14 197 | 24 454 | 570 024 | 0 |
| 3 | 15 010 | 3.926 | 4.790 | 15 157 | 14 197 | 24 617 | 570 024 | 0 |
| 4 | 15 153 | 3.449 | 4.030 | 15 157 | 14 197 | 24 482 | 570 024 | 0 |
| 5 | 15 808 | 3.490 | 3.840 | 17 032 | 14 197 | 24 392 | 570 024 | 0 |

Summary (optimised current):

| Workload | alloc/paint mean (min–max) | avg_ms mean (min–max) | Gen0 |
|---|---|---|---|
| ascii | **15 261** (15 010–15 808) | **3.63** (3.45–3.93) | **0** |
| sgr | **15 547** (15 157–17 032) | **3.65** (3.42–3.86) | **0** |
| cursor | **14 197** (stable) | **2.84** (2.70–3.15) | **0** |
| utf8 | **24 513** (24 392–24 620) | **4.60** (4.33–5.04) | **0** |
| sustained 40 | **570 029** total (~14.3 KB/paint) | — | **0** |

Allocation improvement vs recorded baseline remains ~63–70× and is stable across repetitions (noise ≪ improvement). Full-viewport ascii paint remains ~3.5–4.3 ms (TextRenderer/GDI dominated).

#### CPU / dirty-row measurements (Release STA, 2026-11-19)

Same probe harness (`MeasurePaintAndWrite_RecordsMetrics`), Windows 10.0.26200, .NET 10.0.15, ~120×40.

After dirty-row remediation (pre-correctness-fix sample):

| Workload | avg_ms | avg rows painted | full-invalidate ratio | notes |
|---|---:|---:|---:|---|
| ascii full `Refresh` | ~3.7 | all | n/a | unchanged structural full paint |
| idle `Refresh` (no VT change) | ~3.4 | all | n/a | external Refresh → full paint |
| in-place line (CUP + text) | **~0.23** | **1.0** | **0.00** | dirty-row path |
| cursor-only CUP | **~0.21** | **1.0** | **0.00** | dirty-row path |
| scroll line (LF at bottom) | ~2.9 | ~44 | ~0.95 | `YDisp` change → full |
| sustained 40× line write | 16.4 ms total | **3.1** | — | filling viewport; alloc ~67 KB Gen0 0 |

After correctness fix (same methodology; not a new optimisation claim):

| Workload | avg_ms | avg rows | Gen0 | notes |
|---|---:|---:|---:|---|
| ascii full | ~4.4 | all | 0 | ~15.2 KB/paint |
| in-place line | **~0.19** | **1.0** | 0 | still ~20× vs full |
| cursor-only | **~0.20** | **1.0** | 0 | |
| scroll line | ~2.8 | ~44 | 0 | full-inv ratio ~0.95 |
| sustained 40 | 12.6 ms total | **3.1** | 0 | alloc ~63 KB |

#### Remaining TextRenderer allocations (accepted trade-off)

~15 KB/paint on plain ASCII ≈ ~40 attribute runs × `_runBuilder.ToString()` for `TextRenderer.DrawText` (API requires `string`). Adjacent same-attribute cells are already batched into one run. Further cuts would need a non-`TextRenderer` API or pooled/char buffers with uncertain compatibility benefit; **not justified** given Gen0 is already 0 over 80 paints at 75 ms cadence (~13 paints/s → ~200 KB/s residual vs ~13 MB/s pre-fix).

#### GDI resource lifecycle (source + tests)

| Guarantee | Evidence |
|---|---|
| Dispose exactly once | `Dispose(bool)` → `DisposeGdiCaches` / `_boldFont.Dispose`; caches cleared after dispose |
| No reuse after dispose | Dictionaries cleared; `_isDisposing` / `IsDisposed` short-circuit `OnPaint` / `Write` |
| No dispose mid active paint | WinForms paint is UI-thread synchronous; dispose sets `_isDisposing` before releasing GDI; capacity flush only between `GetBrush`/`GetPen` acquisitions (FillRectangle/DrawLine complete before next lookup) |
| Bounded growth | `MaxCachedGdiObjects = 384`; test `ManyRgbColours_BrushCacheRemainsBounded` |
| Identical attrs reuse | ARGB dictionary hit returns same brush/pen |
| Font change | `OnFontChanged` disposes obsolete `_boldFont` |
| Recreation / dispose | `Dispose_ClearsGdiCaches_AndRecreationPaints`; `Dispose_IgnoresSubsequentDisplayCalls` |

No additional GDI defect found during qualification beyond the RGB unbounded-cache risk already capped.

#### Client regression suite (CPU/repaint + correctness)

```powershell
dotnet test tests/TechConnect.Client.Tests -c Release -- xUnit.MaxParallelThreads=1
```

Result after painted-version fix (Release, sequential): **Failed 0, Passed 323, Skipped 0, Total 323, Duration ~20 s**.

Dirty-row tests expanded: in-place, cursor cross-row, multi-update coalesce, multi-row dirty, nested Write during paint, **painted-version skew (v10/v11)**, multi-nested same row, other-row nested dirty, full-invalidate during partial, scroll→partial, clear/overflow, dispose/recreate, SGR. Probe: `%TEMP%\TechConnect-PERF-UI-01.txt`.

#### Outstanding (PERF-UI-01)

- Scroll/`YDisp` changes still full-repaint (no BitBlt scroll optimization).
- No physical serial soak / ETW UI-responsiveness under real VT boot storms.
- Residual ~15 KB/paint on full `Refresh` `TextRenderer` strings accepted.
- Dirty-row CPU wins are isolate WinForms measurements, not claimed end-to-end operator UX.
- `ContentVersion` is unused (stays 0 in XTerm.NET 3.0.3 for these workloads); row versions + cursor fingerprint are authoritative.

### PERF-SRV-01 — Device RX double allocation (FrameReader + BoundedByteQueue)

| Field | Detail |
|---|---|
| Finding ID | PERF-SRV-01 (PERF-03 + queue copy) |
| Component | `FrameReader` + `BoundedByteQueue.TryEnqueue` / `TryEnqueueOwned` |
| Source | `FrameReader.cs` (payload alloc); `BoundedByteQueue.cs`; `OperatorAttachmentManager.TryEnqueueDeviceRx`; `ConnectorListenerHostedService.HandleSerialData` |
| Evidence | See disposition baseline/after isolate tables |
| Impact | Was ~2× Gen0 traffic on attached SerialData path; wasted alloc on overflow refuse |
| Severity | **High** (pre-fix); allocation double-copy **remediated** |
| Confidence | **High** |
| **Disposition** | **Remediated** (ownership transfer) — FrameReader payload alloc remains (required); queue second copy removed on SerialData path — see § PERF-SRV-01 disposition |

### PERF-SRV-01 disposition (baseline + remediation)

**Verdict: Double-copy on the Server device→operator SerialData path removed via ownership transfer. FrameReader’s per-frame payload allocation remains required and is now the single heap copy into the operator queue.**

#### Data path (confirmed)

```
TLS SslStream read
  → FrameReader.ReadAsync
       allocates byte[payloadLength] after limit check (ownership → ProtocolFrame.Payload)
  → ConnectorListenerHostedService.HandleSerialData
  → OperatorAttachmentManager.TryEnqueueDeviceRx(sessionId, payload)
  → BoundedByteQueue.TryEnqueueOwned(payload)   // AFTER: no ToArray
  → Operator SSH consumer DequeueAsync → write
```

| Buffer | Allocator | Owner after success | Readers | Mutators | Lifetime |
|---|---|---|---|---|---|
| Frame header (6 B) | `FrameReader` instance field | Reader | Reader only | Reader | Connection |
| SerialData payload | `FrameReader` (`new byte[]`) | Queue after `TryEnqueueOwned` | Dequeue consumer | None after transfer | Until SSH write completes / drop / dispose |
| Defensive copy path | `TryEnqueue(ReadOnlyMemory)` `ToArray` | Queue | Same | None | Same (non-SerialData / explicit copy API) |

#### Confirmed allocation sources (pre-fix)

1. **Required:** `FrameReader` payload array — stream read destination; ownership transferred to caller; concurrent readers unsupported; oversized rejected **before** alloc.
2. **Avoidable:** `BoundedByteQueue.TryEnqueue` `data.ToArray()` after FrameReader already owned a dedicated array — second heap copy; previously allocated **before** capacity check (overflow discarded the copy).

ArrayPool deferred: payload can sit in the 256 KiB operator queue under a slow SSH consumer; pool return timing across overflow/detach/cancel is higher risk than the measured win of removing the second copy.

#### Changes implemented

- `BoundedByteQueue.TryEnqueueOwned(byte[])` — capacity check then enqueue **same array** (caller must not reuse on success; retains ownership on refuse).
- `TryEnqueue(ReadOnlyMemory)` — capacity check **before** `ToArray` (overflow no longer allocates a discarded copy).
- `OperatorAttachmentManager.TryEnqueueDeviceRx(string, byte[])` — uses `TryEnqueueOwned`.
- `HandleSerialData` — documents ownership hand-off from `frame.Payload`.

Queue capacity (default 256 KiB), refuse→detach overflow, session isolation, and completion/dispose behaviour unchanged.

#### Baseline isolate (before production change)

Command (temp Release probe, algorithm mirror): FrameReader-style `new byte[n]` + `ReadOnlyMemory.ToArray()`.  
Environment: Windows 10.0.26200, .NET 10.0.15.

| Payload | Alloc/frame before | Ops/s before | Gen0 Δ |
|---:|---:|---:|---:|
| 64 B | 176 | ~21.3M | 0 |
| 256 B | 560 | ~21.2M | 1 |
| 1 KiB | 2 096 | ~6.5M | 6 |
| 4 KiB | 8 240 | ~2.6M | 26 |
| 16 KiB | 32 816 | ~0.74M | 20 |
| 64 KiB (max SerialData) | 131 120 | ~0.13M | 83 |

Projected ownership transfer (same probe, enqueue without `ToArray`): **88 / 280 / 1 048 / 4 120 / 16 408 / 65 560** alloc/op (~1× payload).

#### After — real `BoundedByteQueue` isolate (Release test probe)

Command: `dotnet test tests/TechConnect.Server.Tests -c Release --filter MeasureServerRxEnqueue_RecordsMetrics`  
Output: `%TEMP%\TechConnect-PERF-SRV-01.txt`

| Payload | Alloc/frame copy path | Alloc/frame owned path | Ops/s copy | Ops/s owned | Gen0 copy | Gen0 owned |
|---:|---:|---:|---:|---:|---:|---:|
| 64 B | 248 | **160** | ~2.6M | ~4.2M | 0 | 0 |
| 256 B | 632 | **352** | ~2.8M | ~3.2M | 2 | 1 |
| 1 KiB | 2 168 | **1 120** | ~2.1M | ~2.6M | 6 | 3 |
| 4 KiB | 8 312 | **4 192** | ~1.1M | ~1.6M | 26 | 13 |
| 16 KiB | 32 888 | **16 480** | ~0.37M | ~0.67M | 20 | 10 |
| 64 KiB | 131 192 | **65 632** | ~0.10M | ~0.19M | 83 | 41 |

**Measured improvement:** ~50% cut in enqueue-path heap traffic (second payload copy eliminated). Gen0 roughly halved at larger sizes. Throughput improved modestly on the isolate (less copy CPU). Remaining alloc ≈ one payload array + small queue/drain overhead (not zero).

Framed integration path (FrameWriter+MemoryStream+FrameReader per op) still shows large fixed overhead; **delta** copy→owned ≈ one payload length — consistent with the isolate.

Sustained owned (1024 B × 20 000, drain every 8): alloc/op **3744** (includes framing), max queued **8192**, Gen0 **4** — queue stayed bounded.

#### Correctness

- Protocol `FrameReaderWriterTests`: **19 passed** (incl. ownership distinct arrays, max SerialData, oversized-before-alloc).
- Server focused (queue/manager/forwarding/perf/privilege serial): **38 passed**.
- Full Server suite: **1230 passed**; **4 failed** unrelated to this change — 3× missing `appsettings.Development.json` (only `.example` present in tree); 1× `EnableDisablePrivilegeTests.Disable_WhileTerminalActive` timeout flake (passed on re-run).

#### Remaining copies / limitations

- **FrameReader** still allocates per non-empty frame (PERF-PROTO-01) — required for current ownership model; ArrayPool deferred.
- Defensive `TryEnqueue(ReadOnlyMemory)` copy remains for non-owned callers.
- TLS/SSH write buffers downstream are out of scope.
- No multi-host TCP/mTLS soak in this remediation; measurements are in-process Release isolates + framed MemoryStream.

### PERF-CLI-01 — Serial RX ownership copy every read

| Field | Detail |
|---|---|
| Finding ID | PERF-CLI-01 (prior PERF-04) |
| Component | `SerialConnectionService.ReadLoopAsync` |
| Source | `SerialConnectionService.cs` (RX ownership decision after `ReadAsync`) |
| Evidence | See disposition baseline/after tables |
| Impact | Was steady Gen0 on **every** RX read; local-console-only path no longer allocates a durable payload |
| Severity | **Medium** (pre-fix); **partially remediated** |
| Confidence | **High** |
| **Disposition** | **Partially remediated** (2026-11-19) — local-only ephemeral read-buffer view; remote-durable copy retained — see § PERF-CLI-01 disposition |

> **Naming note:** Earlier task “PERF-CLI-01/02” for `SerialConsoleBuffer` maps to **PERF-CLI-02**. This section is serial RX allocation/ownership only.

### PERF-CLI-01 disposition (baseline + remediation)

**Verdict: Local-console-only RX no longer allocates `new byte[read]` per read. When remote ingress is enabled, one durable payload array remains required (Incoming / outbound retain it asynchronously). PERF-CLI-02 ring buffer left intact.**

#### Data path (confirmed)

```
SerialPort.BaseStream.ReadAsync(reusable 4 KiB buffer)
  → if remote ingress enabled:
        AllocateUninitializedArray(read) + BlockCopy  → SerialDataChunk (durable)
        → PublishRemoteIngress → Incoming → RemoteOutboundBuffer (same Memory)
        → DataTransferred → SerialConsoleBuffer.Enqueue (copies into ring)
     else:
        SerialDataChunk.Data = buffer.AsMemory(0, read)  // ephemeral
        → skipped-remote diagnostic += read
        → DataTransferred → SerialConsoleBuffer.Enqueue (copies into ring; must finish before return)
```

| Consumer | Needs durable bytes beyond callback? | Copy/alloc |
|---|---|---|
| `SerialConsoleBuffer` | Yes (ring) | Synchronous `Span` copy into ring |
| `Incoming` / remote outbound | Yes (async queue) | Requires owned array |
| Local-only `DataTransferred` | No if handler copies sync | Ephemeral view OK |

#### Root cause

Reusable read buffer already existed. The avoidable cost was **always** copying into a dedicated `byte[read]` even when remote ingress was disabled and the only consumer (`SerialConsoleBuffer`) copies synchronously during `DataTransferred`. With remote ingress enabled, that owned array is **necessary** — the channel retains the chunk past the next `ReadAsync`.

ArrayPool deferred: return timing across Incoming queue, outbound in-flight, cancel, and detach is higher risk than the remaining remote-path alloc.

#### Baseline isolate (before production change)

Temp Release probe: always `new byte[read]` + console-style copy vs ephemeral local. Windows 10.0.26200, .NET 10.0.15.

| Read | Local before alloc/op | Local after (ephemeral) | Remote alloc/op |
|---:|---:|---:|---:|
| 16 B | 40 | **0** | 40 |
| 64 B | 88 | **0** | 88 |
| 256 B | 280 | **0** | 280 |
| 1 KiB | 1048 | **0** | 1048 |
| 4 KiB | 4120 | **0** | 4120 |

Copies/read: local before = 1 BlockCopy + ring copy; local after = ring copy only; remote = 1 BlockCopy + ring copy (unchanged necessity).

#### After — ownership probe with real ring enqueue (Release test)

Command: `dotnet test … --filter MeasureLocalVsRemoteOwnership_RecordsMetrics`  
Output: `%TEMP%\TechConnect-PERF-CLI-01.txt`  
(Includes periodic `TryDrain` allocs — not pure ownership isolate.)

| Read | Local alloc/op | Remote alloc/op | Notes |
|---:|---:|---:|---|
| 64 B | 123 | 211 | Local avoids durable payload; drain/`SerialDataChunk` overhead remains |
| 1 KiB | 1083 | 2131 | Remote ≈ durable payload + console/drain traffic |
| 4 KiB | 4155 | 8275 | ~2× remote vs local at large reads |

**Claim:** measurable **allocation reduction on local-console-only** path (eliminate per-read durable payload). Remote path allocation **not** eliminated (by design). Throughput improvement claimed only for the pure isolate (ephemeral local); production path remains baud/UI bound.

#### Correctness

- Focused serial/buffer/ingress: **76 passed** (incl. durable Incoming retention, local console distinct chunks, short/full reads).
- Full Client suite Release sequential: **302 passed**, 0 failed.
- PERF-CLI-02 ring + overflow recovery tests remain green (untouched).

#### Remaining allocations

- Remote-durable `byte[read]` when ingress enabled.
- `SerialDataChunk` object per publish.
- Ring `TryDrain` `byte[]` for UI.
- TX path still `data.ToArray()` for UI notification (out of PERF-CLI-01 RX scope).

#### Correctness review (2026-11-19) — event contract & ingress races

| Topic | Finding |
|---|---|
| Ownership decision ↔ Incoming | Coupled: ephemeral path never calls `PublishRemoteIngress`; durable path only when ingress observed enabled at decision time |
| Ingress enable after ephemeral read | Prior chunk stays local-only; next reads allocate durable — **no ephemeral on Incoming** |
| Ingress disable | Clears flag then spins on `_ingressPublishInFlight` before drain (PERF-CLI-03) |
| Production `DataTransferred` subscribers | `MainForm` → `SerialConsoleBuffer.Enqueue` (sync ring copy); loopback probe `AddRange(Span)` (sync copy) |
| API limitation | CLR events cannot enforce ephemeral lifetime; documented on `SerialDataChunk` / `ISerialConnectionService`; incorrect retention is a subscriber bug |
| Skipped counter | Includes disabled/raced-disable, failed `TryWrite`, and post-stop drain; docs clarified |

No production ownership logic change required by the review; documentation and contract tests strengthened. Physical serial soak still outstanding.

### PERF-CLI-02 — SerialConsoleBuffer per-byte enqueue and front `RemoveRange`

| Field | Detail |
|---|---|
| Finding ID | PERF-CLI-02 (prior PERF-01); task also called PERF-CLI-01 for per-byte enqueue |
| Component | `SerialConsoleBuffer` |
| Source | `SerialConsoleBuffer.cs` (ring buffer) |
| Evidence | See disposition baseline/after tables |
| Impact | Was moderate CPU when draining; **material** under soft-cap overflow / UI stall |
| Severity | **Medium** (pre-fix); buffer path **remediated** |
| Confidence | **High** |
| **Disposition** | **Remediated** (fixed ring + bulk append) — see § PERF-CLI-02 disposition |

### PERF-CLI-02 disposition (baseline + remediation)

**Verdict: Per-byte `List.Add` and front `RemoveRange` replaced by a fixed-capacity ring with bulk `Span` copies. Soft cap (default 1 MiB), drop-oldest accounting, and `ResyncAfterDisplayOverflow` recovery unchanged.**

#### Data path (confirmed)

```
SerialPort RX → SerialConnectionService (owned chunk)
  → DataTransferred → MainForm → SerialConsoleBuffer.Enqueue (RX only)
  → 75 ms UI timer → TryDrain → SerialTerminalControl.Write
       (if droppedSincePreviousDrain > 0 → WriteAfterDisplayOverflow → VtTerminalHost.ResyncAfterDisplayOverflow / Reset)
```

Transport / remote forwarding is unaffected; display drops never discard serial RX for the remote path.

#### Confirmed bottlenecks (pre-fix)

1. **Per-byte enqueue:** `for` + `_pending.Add(span[i])` — bounds checks / growth pressure under bulk chunks.
2. **Overflow:** `_pending.RemoveRange(0, overflow)` — O(n) element shift when soft cap exceeded under UI stall.
3. **Drain:** `ToArray()` copy to UI (retained; ownership to VT write; no mutable ring exposure).

#### Selected design

Fixed `byte[_maxPendingBytes]` ring with `_head` / `_count`:

- Bulk `Span.CopyTo` on enqueue (wraps once when needed).
- Overflow advances `_head` (O(1)); no element shift.
- Chunk ≥ capacity keeps newest soft-cap tail; accounts prior pending + excess as dropped.
- Single `_gate` lock per enqueue/drain/clear (not per byte).
- ArrayPool deferred — buffer lifetime spans UI stalls up to 1 MiB; drain already allocates the UI copy.

#### Baseline isolate (algorithm mirror, before production change)

Temp Release probe (`%TEMP%\TechConnect-PERF-CLI-01-02`): `List<byte>` per-byte Add + `RemoveRange` vs ring. Windows 10.0.26200, .NET 10.0.15.

| Workload | LIST ops/s | LIST MiB/s | RING ops/s | RING MiB/s | Notes |
|---|---:|---:|---:|---:|---|
| 16 B drained | ~44.0M | ~671 | ~227.8M | ~3477 | Bulk win |
| 256 B drained | ~2.9M | ~709 | ~27.2M | ~6640 | ~9× ops/s |
| 4 KiB drained | ~0.20M | ~785 | ~2.8M | ~10 994 | ~14× ops/s |
| Overflow 1 KiB / 64 KiB cap | ~0.50M | ~491 | ~52.7M | ~51 507 | ~105× ops/s; same dropped count |
| Overflow 4 KiB / 1 MiB cap | ~0.056M | ~219 | ~10.4M | ~40 488 | ~185× ops/s |

Dropped byte counts matched between LIST and RING on overflow cases.

#### After — production `SerialConsoleBuffer` (Release test probe)

Command: `dotnet test … --filter MeasureEnqueueDrainOverflow_RecordsMetrics`  
Output: `%TEMP%\TechConnect-PERF-CLI-01-02.txt`

| Workload | Alloc/op | Ops/s | MiB/s | Gen0 | Max pending | Dropped |
|---|---:|---:|---:|---:|---:|---:|
| 1 B drained | 4 | ~7.3M | ~7 | 0 | 8 | 0 |
| 16 B drained | 19 | ~24.1M | ~367 | 0 | 128 | 0 |
| 64 B drained | 67 | ~20.8M | ~1272 | 0 | 512 | 0 |
| 256 B drained | 259 | ~16.7M | ~4068 | 0 | 2048 | 0 |
| 1 KiB drained | 1027 | ~6.8M | ~6681 | 0 | 8192 | 0 |
| 4 KiB drained | 4099 | ~1.2M | ~4567 | 1 | 32768 | 0 |
| Sustained 256 B | 259 | ~15.5M | ~3773 | 0 | 2048 | 0 |
| Overflow 1 KiB / 64 KiB | **0** | ~18.4M | ~17 960 | 0 | 65536 | 20 414 464 |
| Overflow 4 KiB / 1 MiB | **0** | ~7.4M | ~28 994 | 0 | 1 048 576 | 15 335 424 |
| Slow consumer 256 B | 257 | ~15.6M | ~3813 | 0 | 4096 | 0 |

Drained-path alloc/op ≈ chunk size (UI `TryDrain` array); overflow path **0** alloc/op. No physical serial hardware in this qualification.

#### Overflow / UTF-8 / VT correctness

- Soft cap and drop-oldest retained; `PendingBytesDropped` reported on next `TryDrain` then cleared.
- MainForm still calls `WriteAfterDisplayOverflow` → `ResyncAfterDisplayOverflow` (`Terminal.Reset`) when drops &gt; 0.
- Existing `DisplayOverflowRecoveryTests` (mid-UTF-8, mid-CSI, fragmented no-overflow) remain green.

#### Correctness / suite

- Focused buffer/overflow/terminal/RX: **63 passed**.
- Full Client suite Release sequential: **Failed 0, Passed 298, Skipped 0** (~19 s).

#### Outstanding

- Audit **PERF-CLI-01** partially remediated (local ephemeral; remote durable remains).
- Drain still copies to a new `byte[]` for the UI (required ownership boundary).
- No physical serial soak / ETW under real UI stall.

### PERF-CLI-03 — Unbounded Incoming Channel without consumer when remote unconfigured

| Field | Detail |
|---|---|
| Finding ID | PERF-CLI-03 |
| Component | `SerialConnectionService` + `RemoteSessionService.StartAsync` |
| Source | Unbounded `Channel.CreateUnbounded` (`SerialConnectionService.cs`); early return at L242–252 skips `_rxBridgeTask` |
| Evidence | Code observation; comments elsewhere acknowledge need to drain Incoming |
| Impact | Unbounded memory growth for local-console-only sessions while port is open |
| Severity | **High** (reliability/memory; worsens under sustained RX) |
| Confidence | **High** for code path; not load-tested in this audit |
| **Disposition** | **Fully remediated and qualified** (2026-11-19) — see § PERF-CLI-03 disposition |

### PERF-CLI-03 disposition (investigation + remediation + verification + qualification)

**Verdict: Fully remediated and closed.** Not a false positive relative to REL-02. Ingress gating closes the no-consumer windows; a TOCTOU race found in verification was fixed with an in-flight publish counter; exception-safety and DevCerts-backed Client suite were qualified green.

#### REL-02 reconciliation

REL-02 correctly ensures that, **while a configured remote session is active**, `BridgeSerialRxAsync` always consumes `Incoming` (enqueue when connected, `Discard` when not) and, on unexpected channel-level failure, runs `DiscardIncomingAfterBridgeFailureAsync` until session cancellation. Those paths remain intact.

REL-02 did **not** cover unconfigured start, post-stop, or post-finalize with serial still open — those were the PERF-CLI-03 gaps.

#### Remediation

- Ingress gate (`SetRemoteIngressEnabled` / `DrainPendingRemoteIngress`) — default **off** on open.
- `RemoteSessionService` arms ingress only with the bridge; disables **before** cancel; drains after bridge join.
- **Verification fix:** `PublishRemoteIngress` uses `_ingressPublishInFlight` + double-check; `SetRemoteIngressEnabled(false)` spins until in-flight is zero before returning, so a subsequent drain cannot miss a late `TryWrite`. No lock allocation on the RX hot path (Interlocked + `SpinWait` only).

#### Producer / disable / drain race (verification)

| Question | Result |
|---|---|
| Was TOCTOU possible (check enabled → disable/drain → late TryWrite)? | **Yes** on the first remediation |
| After in-flight wait? | **No** — disable returns only after publishers that observed enabled=1 finish; then drain; later publishes see enabled=0 |
| Producer blocked indefinitely? | **No** — `TryWrite` remains non-blocking; disable only spins while in-flight ≠ 0 |
| Deadlock with shutdown? | **No** — serial RX does not wait on session lifecycle; session waits briefly on in-flight then joins bridge |

Regression: `ConcurrentRxDuringDisableDrain_LeavesNoAbandonedIncoming`, `ConcurrentDisableDrain_UnderSustainedRx_LeavesIncomingEmpty`.

#### Ingress counter exception safety (qualification)

`PublishRemoteIngress` structure (source-verified):

1. Fast-path `Volatile.Read(enabled)==0` → skip (**no** increment).
2. `Interlocked.Increment(_ingressPublishInFlight)` — full fence.
3. `try` { second `Volatile.Read(enabled)`; optional `TryWrite`; accounting / log }.
4. `finally` { `Interlocked.Decrement(_ingressPublishInFlight)` }.

| Property | Evidence |
|---|---|
| Exactly one decrement per successful increment | All post-increment exits are inside `try`; `finally` always runs |
| Failed `TryWrite` | Returns false → else branch; still hits `finally` |
| Unexpected exceptions (e.g. logging) | `finally` still decrements — disable cannot spin forever |
| Early return after second enabled check | Inside `try` → `finally` |
| Counter cannot go negative under correct pairing | Increment only on the publish path; Decrement only in matching `finally` |
| Memory ordering | `Interlocked.Increment`/`Decrement` provide acquire/release; disable’s `Volatile.Write(enabled,0)` then spin on `Volatile.Read(inFlight)` cannot observe zero while a publisher that saw enabled=1 is still between increment and decrement |
| Publication after disable completes | Impossible: either first check sees 0, or second check after join sees 0 and skips `TryWrite`, or `TryWrite` completes before disable observes inFlight==0 |

No additional lock/alloc on the RX hot path. Regression: `CompletedIncoming_FailedPublish_DoesNotHangDisable`, `FailedTryWrite_DoesNotLeakInFlight_OrHangDisable`.

#### Active-session backpressure (verification)

| Stage | Capacity / behaviour |
|---|---|
| Serial RX → `Incoming` | Unbounded **type**, but published only while ingress+bridge armed; bridge `await foreach` + sync `Enqueue`/`Discard` (never awaits transport) |
| Bridge → `RemoteOutboundBuffer` | **Bounded** (default 4 MiB), drop-oldest, non-blocking `Enqueue` |
| Outbound drain → TCP/mTLS | May block on send; **does not** stall the bridge; queue stays within byte budget (+ at most one in-flight chunk) |
| Serial RX under slow remote | **Not blocked** by remote; UI path independent |

**Conclusion:** Effective remote memory bound during an active session is **`RemoteOutboundBuffer`**. `Incoming` is a handoff that stays drained while the bridge runs. Pathological bridge-task hang could theoretically allow `Incoming` growth (same class of single-consumer risk as before); not observed in tests. No pipeline redesign required.

#### SessionPing correction (separate from PERF-CLI-03)

| Item | Detail |
|---|---|
| Failure | `InvalidOperationException: No System.Text.Json type info is registered for <>f__AnonymousType0` |
| Site | Test `SessionPingTests` and production `TcpMtlsRemoteConsoleTransport.DispatchSessionPing` used `ControlProtocol.Create(..., new { }, ...)` |
| Classification | **Test expectation defect + production AOT bug** — **not** a performance finding; not caused by PERF-CLI-03 |
| Fix | Use registered `SessionPingMessage` in transport + test (**not** a ControlProtocol change; **not** a performance optimisation) |

#### Development certificates (qualification)

Tooling: `tools/TechConnect.Client.DevCerts` (`provision`, then `provision --confirm-trust --yes`).

| Role | Store | Result |
|---|---|---|
| CA (private) | `CurrentUser\My` | Valid, identity match |
| CA (public trust) | `CurrentUser\Root` | Installed; identity match |
| Server (Server Auth EKU) | `CurrentUser\My` | Valid; SANs localhost / 127.0.0.1 / ::1 |
| Client (Client Auth EKU) | `CurrentUser\My` | Valid |

No certificates removed. Local registry / companion / `appsettings.Development.json` thumbprints written (gitignored). Private keys not logged.

#### Previously failing transport tests (qualification)

All six now **pass** after DevCerts provisioning (environmental root cause confirmed):

| Test | Result |
|---|---|
| `Connect_AcceptsValidServerCertificateAndHostname` | Passed |
| `Connect_RejectsUntrustedServerCertificate` | Passed |
| `Connect_RejectsHostnameMismatch` | Passed |
| `Connect_Cancellation_Works` | Passed |
| `Teardown_RemainsBounded` | Passed |
| `RemoteAbort_TeardownRemainsBounded_WithoutSerialCoupling` | Passed |

#### Client test results (qualification — green)

Command:

```text
dotnet test tests/TechConnect.Client.Tests -c Release -- xUnit.MaxParallelThreads=1
```

| Result | Count |
|---|---:|
| Passed | **285** |
| Failed | **0** |
| Skipped | **0** |
| Total | **285** |
| Duration | ~14 s |

Focused ingress/bridge/SessionPing/lifecycle filter: **25 passed**, 0 failed.

#### Measurements (verification)

| Workload | Result |
|---|---|
| 5k×1 KiB always-on, no consumer | pending **5000** |
| Gate-off path | pending **0**, skipped 5 120 000 |
| 8 producers × 2000 with in-flight disable/drain | pending after tasks **0** |
| Gate hot-path micro (2M ops) | ~**7.5 ns/op** (Interlocked + volatile checks; no channel) |

Limitations: race/backpressure conclusions for production SerialConnectionService also rely on unit tests with fakes; mTLS suite not run without DevCerts; no physical serial soak.

#### Outstanding limitations

- Active-session `Incoming` remains unbounded *as a channel type*; bound is behavioural (bridge + outbound buffer).
- DevCerts are machine-local (CurrentUser stores); other hosts must run `provision` + `--confirm-trust` before mTLS tests.
- No physical serial soak under prolonged unconfigured / reconnect load.
- Linux / multi-host E2E qualification remains outside this Client-only suite.

### PERF-PROTO-01 — Per-frame payload `byte[]` allocation in FrameReader

| Field | Detail |
|---|---|
| Finding ID | PERF-PROTO-01 (prior PERF-03 core) |
| Component | `TechConnect.Protocol.Framing.FrameReader` |
| Source | `FrameReader.cs` L128–130 |
| Evidence | Measured ~payload+48 B per frame; no ArrayPool in Protocol |
| Impact | Expected ownership-transfer design; GC scales with frame rate × size |
| Severity | **Medium** |
| Confidence | **High** |

### PERF-PROTO-02 — Flush after every frame write

| Field | Detail |
|---|---|
| Finding ID | PERF-PROTO-02 |
| Component | `FrameWriter.WriteAsync` |
| Source | `FrameWriter.cs` L136 |
| Evidence | Code observation; not measured under SslStream |
| Impact | Potential TLS record / syscall amplification under bursty small frames |
| Severity | **Medium** |
| Confidence | **Medium** (hypothesis for TLS cost) |

### PERF-SRV-02 — Global registry lock on every frame (`TouchActivity`)

| Field | Detail |
|---|---|
| Finding ID | PERF-SRV-02 |
| Component | `ConnectorListenerHostedService` → `ConnectorSessionRegistry.TouchActivity` |
| Source | Listener protocol loop; registry `TouchActivity` / `ConnectorSession.LastActivityUtc` |
| Evidence | Code observation + Release contention probe (see disposition) |
| Impact | Cross-connector contention under many busy connectors |
| Severity | **Medium** at high fan-out; **Low** for single-digit connectors |
| Confidence | **High** |
| **Disposition** | **Fully qualified** (2026-11-19) — cached session + monotonic ObserveActivity; see § PERF-SRV-02 disposition |

### PERF-SRV-02 disposition (investigation + remediation + qualification)

**Verdict: Confirmed and fully qualified for the stated claim (remove global registry lock from per-frame activity touch). Every post-Hello framed message previously took the registry’s global `_gate` via `TouchActivity`. Hot path now calls `RegisteredSession?.ObserveActivity(...)` without that lock. PERF-SRV-01 owned-queue path unchanged. Not an end-to-end throughput claim.**

#### Original finding

Listener `RunProtocolAsync` called `_sessionRegistry.TouchActivity(connectionId, utcNow)` under `lock (_gate)` with dictionary lookup on **every** frame after registration. `LastActivityUtc` is display/snapshot metadata (CLI/NOC); not used for idle eviction.

#### Data path (activity touch in context)

```
FrameReader.ReadAsync
  → (registered) RegisteredSession?.ObserveActivity(utcNow)   // AFTER: no registry lock
  → SerialData → TryEnqueueOwned (PERF-SRV-01) → SSH DequeueAsync
  → other message types handled as before
```

Payload size does **not** drive this finding (timestamp write only). Queue alloc/copy remains PERF-SRV-01.

#### Cached-session lifecycle guarantees (qualification)

| Step | Behaviour |
|---|---|
| New connection | `RegisteredSession` / `RegisteredSessionId` null |
| Hello success | Assigned only after `TryRegister` returns a session |
| Failed / duplicate Hello | Cache not assigned; connection continues without hot-path touch |
| Frame RX | `RegisteredSession?.ObserveActivity` — no dictionary / no `_gate` |
| Disconnect / unregister | Cache cleared to null after unregister, before dispose |
| Reconnect | New `ConnectorConnection` instance; cache set again only after Hello success |

Stale cache cannot update a replacement session under a reused connection id: teardown nulls the cache on the old connection object; `TouchActivity(connectionId)` looks up membership under `_gate` and no-ops when unregistered.

#### Timestamp concurrency semantics (qualification)

- Storage: `long` UTC ticks via `Volatile.Read` / `Volatile.Write` (`LastActivityUtc` get/set).
- Hot path + `TouchActivity`: `ObserveActivity` — CAS loop that only advances when candidate ticks are strictly greater (monotonic under concurrent writers).
- Readers may observe a slightly stale value; conversion uses `DateTimeOffset(ticks, TimeSpan.Zero)`.
- Deterministic test: `ObserveActivity_ConcurrentWriters_PreservesMaximum`.
- Allocation: `ObserveActivity_alloc_bytes_per_50k=0` (Release probe).

#### Baseline (before) — Release contention isolate

Temp probe + in-repo `TouchActivityPerfTests` (Windows 10.0.26200, .NET 10.0.15), 100 sessions:

| Threads | Registry TouchActivity ops/s | ns/op | Cached session write ops/s | ns/op |
|---:|---:|---:|---:|---:|
| 1 | ~14.5M–24.6M | ~41–69 | ~35M–147M | ~7–29 |
| 4 | ~8.3M–13.6M | ~73–121 | ~180M–442M | ~2–6 |
| 8 | ~6.7M–12.7M | ~79–150 | ~242M–481M | ~2–4 |
| 16 | ~5.3M–12.8M | ~78–188 | ~362M–599M | ~2–3 |

Registry path **does not scale** with fan-out (ops/s flat/down); cached path **scales**. Lookup-under-lock then write-outside remains registry-bound.

#### Changes

- `ConnectorSession.LastActivityUtc` — `Volatile` UTC-tick storage + `ObserveActivity` monotonic CAS.
- `ConnectorConnection.RegisteredSession` — set at Hello success; cleared on disconnect teardown.
- `RunProtocolAsync` — `RegisteredSession?.ObserveActivity` (no registry gate).
- `TouchActivity(connectionId, …)` — retained; lookup under lock, `ObserveActivity` outside (non-hot callers).

#### After — qualification contention probe (Release, production types)

Machine: Windows NT 10.0.26200.0, runtime 10.0.15. Methodology: `TouchActivityPerfTests.MeasureTouchActivityContention_RecordsMetrics` — 100 sessions, 40 000 ops/thread, warmup + timed `Parallel.For`; results in `%TEMP%\TechConnect-PERF-SRV-02.txt`. Cached path uses `ObserveActivity` (mirrors production).

| Threads | Registry TouchActivity ops/s | ns/op | Cached ObserveActivity ops/s | ns/op |
|---:|---:|---:|---:|---:|
| 1 | ~38.3M | ~26 | ~590M | ~1.7 |
| 4 | ~14.7M | ~68 | ~673M | ~1.5 |
| 8 | ~17.2M | ~58 | ~1.11B | ~0.9 |
| 16 | ~16.5M | ~60 | ~1.91B | ~0.5 |

Allocations: **0** bytes / 50 000 `ObserveActivity` calls. Claim is **reduced cross-connector lock contention on activity touch**, not end-to-end Server Mbps.

#### Correctness / test qualification (2026-11-19)

| Run | Result |
|---|---|
| `Disable_WhileTerminalActive_KeepsTerminalAtLevel1` (solo) | **Passed** (1/1). Prior suite timeout: second detach waited on already-present `"still attached"` from first detach — test sync bug, not PERF-SRV-02. Fixed with occurrence count + `!IsTerminalActive`. |
| `ScenarioA_Development_…` (solo) | **Passed** (1/1). Prior listen timeout: SSH harness flake (ephemeral port / bind race under suite load), not PERF-SRV-02. |
| Privilege / registry / queue focused | **Passed** |
| Full Server suite sequential (`xUnit.MaxParallelThreads=1`) | **1239 passed, 0 failed** (~3 m 32 s) at PERF-SRV-02 close; consolidation re-run **1241 passed** (see § J) |
| PERF-SRV-01 (`TryEnqueueOwned` / BoundedByteQueue) | Intact; queue tests green in suite |

SSH harness hardening (test-only): `SshTestHost` waits on host lifetime (30 s ceiling), fail-fast when host stops before listen, up to 3 start attempts on new ports after ephemeral-port races. `ConcurrentOutputWrites_AreSerialized` waits for help output before `exit` (teardown race).

#### Remaining limitations

- No multi-host / 100-connector soak; contention probe is in-process isolate only.
- `List()` still takes `_gate` (infrequent CLI/NOC).
- PERF-SRV-03 SSH `FlushAsync` remediated separately (2026-11-19).
- Local gitignored `appsettings.Development.json` must exist for default-admin tests (restored from example when missing).

### PERF-SRV-03 — SSH `WriteRawAsync` flushes under per-session write gate

| Field | Detail |
|---|---|
| Finding ID | PERF-SRV-03 |
| Component | `TechConnectCliSession.WriteRawAsync` |
| Source | CLI session `WriteRawAsync` (write gate + WriteAsync + former FlushAsync) |
| Evidence | Code observation → **measured** Release isolate (see disposition) |
| Impact | Extra await/`Task` overhead on operator outbound; isolated per session |
| Severity | **Low–Medium** (pre); **Low** residual after remediation (baud-limited in practice) |
| Confidence | **High** (after measurement) |
| **Disposition** | **Remediated** (2026-11-19) — removed no-op `FlushAsync`; see § PERF-SRV-03 disposition |

### PERF-SRV-03 disposition (investigation + remediation)

**Verdict: Confirmed as dead flush work on the production stream type, then remediated. Production wraps the SSH channel in `Microsoft.DevTunnels.Ssh.SshStream`: `WriteAsync` already calls `Channel.SendAsync` (data leaves immediately), and `SshStream.Flush()` is a no-op (`CheckDisposed` only). `WriteRawAsync` awaited `FlushAsync` after every gated write, adding ~0.8–1.1 µs/op of await/`Task` overhead with no on-wire effect. PERF-SRV-01 / PERF-SRV-02 unchanged. Not an end-to-end Mbps claim.**

#### Original finding

Audit entry (code observation only): `TechConnectCliSession.WriteRawAsync` took `_writeGate`, wrote, then `FlushAsync` under the same gate. Suspected extra flush latency on operator outbound (device RX → `PumpOperatorOutboundAsync` → SSH). Severity Low–Medium; confidence Medium. Distinct from PERF-PROTO-02 (`FrameWriter` per-frame flush on the connector TLS path).

#### Production path

```
Device RX frame → TryEnqueueOwned (PERF-SRV-01) → BoundedByteQueue
  → PumpOperatorOutboundAsync.DequeueAsync
  → WriteRawAsync: _writeGate → Stream.WriteAsync → (removed) FlushAsync
  → SshStream.WriteAsync → Channel.SendAsync
```

Also used for CLI prompts/text (`WriteTextAsync`). Gate remains; serialization of concurrent writers unchanged. Queue overflow/detach semantics unchanged.

#### Root cause

Not “expensive socket flush”. Decompiled `SshStream`: `Flush()` empty aside from dispose check; `WriteAsync` already sends. Cost was awaiting base `Stream.FlushAsync` (returns `Task`) after every chunk.

#### Baseline (before) — Release isolate

Machine: Windows NT 10.0.26200.0, .NET 10.0.15, 24 logical processors. Probe: gated Write+Flush vs Write-only on SshStream-like stream (`Flush` counts only; `WriteAsync` completes immediately). 50 000 ops (standalone) / 40 000 ops (in-repo `SshOutboundWritePerfTests`).

| Workload | Write+Flush | Write-only |
|---|---:|---:|
| 64 B gated ops/s | ~0.71M–0.97M | ~3.3M–4.2M |
| 64 B ns/op | ~1000–1400 | ~230–300 |
| FlushAsync-only ns/op | ~560–580 | — |

Burst note: coalescing 8 writes + 1 Flush is faster than per-chunk Flush when Flush is a no-op, but production SendAsync already per write — flush coalescing was not selected.

#### Selected change

Remove `await _stream.FlushAsync(...)` from `WriteRawAsync`. Keep `_writeGate` + `WriteAsync`. Document SshStream semantics in remarks.

Rejected alternatives: write-batching/coalescing into one `SendAsync` (broader behaviour change; not required by finding); leaving Flush for “future buffered streams” (misleading; production stream is fixed).

#### After

In-repo probe `%TEMP%\TechConnect-PERF-SRV-03.txt`: write-only ~3.3M ops/s vs write+flush ~0.71M ops/s at 64 B (Release). Regression: `TerminalRxPump_DoesNotFlushAfterEachWrite` — 0 flush calls through connect + 8 RX chunks.

#### Tests

- `SshOutboundWritePerfTests`, `SshOutboundFlushRegressionTests`, `Privilege1_ConnectTxRx…`, BoundedByteQueue / ObserveActivity / TouchActivity probes: **passed**.
- PERF-SRV-01 `TryEnqueueOwned` and PERF-SRV-02 cached `ObserveActivity` paths untouched.

#### Remaining limitations

- Isolate only (no live SSH/TLS multi-host soak).
- Per-chunk `Channel.SendAsync` packetisation unchanged (out of PERF-SRV-03 scope).
- Baud-limited serial workloads remain dominated by link rate, not this await.

### PERF-CLI-04 — Remote→serial defensive copy (and possible second TX notify copy)

| Field | Detail |
|---|---|
| Finding ID | PERF-CLI-04 |
| Component | `RemoteSessionService.HandleRemoteSerialDataAsync` |
| Source | L929–934 `data.ToArray()`; write path also copies for TX `DataTransferred` |
| Evidence | Code observation |
| Impact | Extra allocs on operator→device path; applies receive backpressure (good) |
| Severity | **Medium** |
| Confidence | **High** |

### PERF-CLI-05 — Control frames converted to UTF-16 string before session handling

| Field | Detail |
|---|---|
| Finding ID | PERF-CLI-05 |
| Component | `TcpMtlsRemoteConsoleTransport.DispatchFrameAsync` |
| Source | L508 `Encoding.UTF8.GetString(frame.Payload)` |
| Evidence | Code observation |
| Impact | Extra string alloc on control plane (low rate vs SerialData) |
| Severity | **Low** |
| Confidence | **High** |

### Findings explicitly **not** Critical

No unbounded operator queue, no missing frame-size limit, no lock-held-across-I/O on the Server RX enqueue path after dictionary lookup, and no evidence that a slow SSH consumer stalls other connectors’ protocol readers (detach-on-overflow is intentional and tested).

---

## E. Memory and allocation findings

### E.1 Hotspot summary

| Rank | Location | Pattern | Pooling today |
|---|---|---|---|
| 1 | ~~Client `PaintLine`~~ | ~~Per-cell string concat; brushes/pens per run~~ | **PERF-UI-01 fully qualified** (StringBuilder + GDI cache + dirty rows); residual `TextRenderer` strings ~15 KB/full paint |
| 2 | ~~Server `BoundedByteQueue`~~ | ~~`ToArray` when FrameReader already owns array~~ | **PERF-SRV-01 remediated** (owned enqueue) |
| 3 | `FrameReader` | `new byte[payloadLength]` per non-empty frame | None (PERF-PROTO-01 open) |
| 4 | Client serial RX | Durable `byte[read]` when remote ingress enabled | Scratch 4 KiB; **PERF-CLI-01** local ephemeral done |
| 5 | Client remote→serial | `ToArray` across await | None (PERF-CLI-04 open) |
| 6 | ~~`SerialConsoleBuffer`~~ | ~~Per-byte Add; overflow `RemoveRange`~~ | **PERF-CLI-02 remediated** (ring); drain `ToArray` remains |
| 7 | Control serialize | `SerializeToUtf8Bytes` | N/A (low rate) |

### E.2 ArrayPool / pipelines recommendation stance

| Candidate | Expected benefit | Complexity / risk | Recommendation |
|---|---|---|---|
| ~~Take ownership into `BoundedByteQueue` (no `ToArray`)~~ | **Done** — ~50% cut measured on isolate | Ownership documented; overflow refuses without retaining | **PERF-SRV-01 closed** |
| Check capacity **before** copy | Avoids alloc-on-refuse | Trivial | **Do with ownership change** |
| ArrayPool in FrameReader | Reduces Gen0 if rent/return disciplined | Breaks current `byte[]` ownership; lifetime bugs across TLS/SSH | **Defer** until ownership API redesigned; quantify with BenchmarkDotNet first |
| Pool serial RX chunks (remote-durable only) | Further Gen0 cut when ingress enabled | Channel + outbound must not use after return | **Defer**; local path already allocation-light |
| ~~`SerialConsoleBuffer` ring / `byte[]` slab~~ | **Done** — bulk append + O(1) overflow | — | **PERF-CLI-02 closed** |
| System.IO.Pipelines for framing | Fewer copies under fragmented TCP | Large protocol surface change | **Not justified** until TLS framing benches show need |

### E.3 LOH / unbounded growth

- SerialData max frame **64 KiB** — below classic 85 KiB LOH threshold for individual arrays; LOH pressure from framing alone is unlikely.
- PERF-CLI-03 (undrained Incoming) is **remediated**. A stalled UI filling the 1 MiB console buffer remains the primary **soft-bounded** Client display memory risk.
- Server operator queue is hard-capped at **256 KiB** per attachment.

---

## F. Concurrency and backpressure findings

| Structure | Bound | Policy | Isolation |
|---|---|---|---|
| Client `Incoming` Channel | Unbounded **but gated** | Published only while remote ingress enabled; otherwise skipped (diagnostics) | PERF-CLI-03 remediated |
| Client `RemoteOutboundBuffer` | 4 MiB default | Drop oldest; serial never blocked | Per session |
| Client `SerialConsoleBuffer` | 1 MiB soft | Drop oldest **display** bytes | Display only |
| Server `BoundedByteQueue` | 256 KiB default | Refuse → detach operator | Per attachment; connector survives |
| Connector accept capacity | 100 default | Reject new TCP immediately | Global connector cap |
| SSH sessions | 50 default | Accept then disconnect over limit | Global SSH cap |
| `FrameWriter` | SemaphoreSlim(1,1) | Serialize writes | Per connection |
| CLI SSH writes | `_writeGate` | Serialize writes (Flush removed — PERF-SRV-03) | Per operator session |
| `ConnectorSessionRegistry` | Single `_gate` | Membership mutations; activity touch no longer on RX hot path | Cross-connector for register/list only |

**Throughput vs latency**

- Throughput ceilings for serial console are typically **baud-limited** (e.g. 115200 ≈ 11.5 KB/s) — framing CPU is not the limiter on a modern host.
- Latency and UI responsiveness are more sensitive to **per-frame flush**, **UI paint**, and **queueing delay** before detach/drop.

**Slow consumer**

- Slow SSH operator → fills `ToOperator` → detach (`OperatorBackpressure`); other sessions unaffected (**fact**, integration-tested).
- Slow serial write on Client ← remote SerialData → awaits handler → TCP/TLS receive backpressure on **that** connection only (**fact** by design).
- Slow UI → display drops / expensive paint; **does not** stop remote forward path (**fact**).

**Thread-pool starvation**

- Per-connection `Task.Run` for connectors and CLI shells; defaults (100 connectors / 50 SSH) are moderate. No evidence of sync-over-async on the SerialData hot path. Starvation is a **hypothesis** only under extreme misconfiguration.

---

## G. Recommended benchmarks

Implement as a **new test/bench project** (not production). Prefer BenchmarkDotNet for microbenchmarks; custom hosts for integration/sustained load.

| # | Benchmark | Type | Workload | Payload | Concurrency | Duration | Measure | Success criteria | Hardware needs |
|---|---|---|---|---|---|---|---|---|---|
| 1 | Frame read/write | Micro | MemoryStream / Pipe | 16 / 64 / 1K / 4K / 64K | 1 | 1–5 s iters | ops/s, alloc/op, Gen0 | alloc/op documented; regression gate ±10% | None |
| 2 | Control JSON ser/de | Micro | Source-gen DTOs | typical Hello/OperatorStatus | 1 | — | ns/op, alloc | Stable alloc | None |
| 3 | TCP cleartext framing | Integration | loopback TcpClient | 1K SerialData | 1 and 10 conn | 30–60 s | MB/s, p99 latency | ≥ X MB/s local | None |
| 4 | TCP + mTLS framing | Integration | DevCerts loopback | same | 1 and 10 | 30–60 s | MB/s vs cleartext; CPU% | TLS overhead quantified | Dev certs |
| 5 | Client serial RX/TX | Integration | fake `ISerialPort` | chunk 64–4K | 1 | 30 s | alloc/s, channel depth | No unbounded growth; drain bridge always on | None |
| 6 | VT parse (`VtTerminalHost`) | Micro | recorded boot logs | burst + steady | 1 | — | ns/KB, alloc | Parser ≪ paint | None |
| 7 | Terminal paint | Integration | headless or message-filter | 80×24 / 120×40 @ 75 ms | 1 | 30 s | UI thread CPU, alloc/s, frame time | UI CPU &lt; budget under 115200 | Windows UI |
| 8 | Server connector fan-in | Sustained | N fake connectors | 1K @ rate R | N=10/50/100 | 5–10 min | CPU, GC, `TouchActivity` wait | Linear-ish scaling; no cross-talk | Multi-core |
| 9 | Operator SSH concurrency | Sustained | M attached sessions | RX fill toward 256 KiB | M=10/50 | 5 min | detach correctness, CPU | Overflow only affects slow op | SSH test host |
| 10 | E2E forward latency | Integration | Client+Server+fake serial | 64–1K | 1 | — | one-way and RTT latency | p99 under SLA | Ideally 2 hosts; serial optional |

**Distinguish:** micro (alloc/CPU of a function) vs integration (loopback TLS) vs sustained (GC/pause over minutes).

---

## H. Prioritized remediation plan

Do **not** implement until this audit is reviewed. Order by expected impact × confidence ÷ (complexity × risk).

| Priority | Action | Expected improvement | Evidence | Complexity | Regression / security risk |
|---|---|---|---|---|---|
| **1** | ~~Fix paint path: StringBuilder + brush/pen cache + dirty rows~~ | **Done (PERF-UI-01)** — alloc + dirty-row CPU | **Fully qualified** 2026-11-19 | — | Scroll still full repaint; no BitBlt |
| **2** | ~~Always drain or disable Incoming when remote unconfigured / bridge down~~ | **Done (PERF-CLI-03)** — ingress gate + drain on stop/finalize | Remediated 2026-11-19 | — | — |
| **3** | ~~`BoundedByteQueue`: take ownership / copy only after capacity check~~ | **Done (PERF-SRV-01)** — ~50% enqueue alloc cut | Remediated 2026-11-19 | — | FrameReader alloc remains |
| **4** | ~~`SerialConsoleBuffer`: bulk copy + ring; avoid `RemoveRange`~~ | **Done (PERF-CLI-02)** — overflow ~100× isolate ops/s | Remediated 2026-11-19 | — | Drain copy remains |
| **4b** | ~~Local RX ephemeral buffer when ingress disabled~~ | **Done (PERF-CLI-01 partial)** — 0 durable alloc/op local isolate | Remediated 2026-11-19 | — | Remote durable alloc remains |
| **5** | FrameWriter: coalesce header+payload; reconsider per-frame Flush (batch under gate) | Lower TLS syscall cost | PERF-PROTO-02 hypothesis | Medium | Must not interleave frames; validate with TLS bench |
| **5b** | ~~SSH CLI `WriteRawAsync`: drop no-op `FlushAsync`~~ | **Done (PERF-SRV-03)** — SshStream.Flush is no-op | Remediated 2026-11-19 | — | Per-chunk SendAsync unchanged |
| **6** | ~~Reduce `TouchActivity` contention (cached session / Volatile timestamp)~~ | **Done (PERF-SRV-02)** — hot path no registry lock | **Fully qualified** 2026-11-19 | — | Display-only; no 100-connector soak |
| **7** | Optional: pool or reuse serial RX / frame payloads | Further Gen0 reduction | PERF-PROTO-01 / CLI-01 | **High** | Ownership bugs — only after benches |
| **8** | Control path: pass `ReadOnlyMemory<byte>` instead of `GetString` | Minor alloc win | PERF-CLI-05 | Low | Low |

**Explicit non-goals for remediation**

- Do not disable TLS validation, weaken mTLS, remove frame limits, or unbounded-queue “for speed”.
- Do not log serial payloads.
- Do not share mutable buffers across connections without a formal lease model.

---

## I. Outstanding qualification

Cannot be fully validated without:

| Gap | Why it matters |
|---|---|
| Physical serial hardware at target baud | Driver buffering, USB latency, real chunk sizes |
| Live Client ↔ Server ↔ SSH operator on separate hosts | True E2E latency, TLS, and scheduling |
| Linux Server runtime execution | Cross-compiled ELF verified; runtime on Linux host not qualified |
| Live production mTLS integration | DevCerts loopback suite ≠ production PKI |
| Client UI smoke on published self-contained binary | Publish script verifies PE layout; GUI smoke is manual |
| Sustained multi-hour soak | Leak / Gen2 / LOH growth over time |
| WinForms ETW / process GDI-handle soak under real VT boot storms | PERF-UI-01 allocation closed; residual UI-CPU / handle soak still open |
| Production connector counts near capacity (100) | Validates PERF-SRV-02 contention claim beyond isolate |
| BenchmarkDotNet formal suite in CI | Reproducible gates beyond this one-off probe |

---

## J. Consolidation disposition table (release-readiness)

Verified against implementation + suite evidence on 2026-11-19. Microbenchmarks remain microbenchmarks; contention probes are not end-to-end Mbps claims.

| Finding | Disposition | Evidence class |
|---|---|---|
| **PERF-CLI-01** | **Partially remediated** | Local ephemeral RX; remote durable alloc retained; CLR event lifetime documentation-enforced; no physical serial soak |
| **PERF-CLI-02** | **Remediated** | Fixed ring + bulk `Span`; O(1) overflow; capacity/drop accounting; VT reset after display overflow |
| **PERF-CLI-03** | **Fully remediated** | Ingress gate + in-flight publish counter; Incoming empty when unconfigured (additional finding beyond the six-ID summary table) |
| **PERF-UI-01** | **Fully qualified** | StringBuilder + GDI cache + dirty rows; paint-plan snapshot; pre-paint fingerprint; re-dirty on version skew; v10/v11 regression present |
| **PERF-SRV-01** | **Remediated** | `TryEnqueueOwned`; refuse leaves caller ownership; capacity/overflow unchanged |
| **PERF-SRV-02** | **Fully qualified** | `RegisteredSession` + monotonic `ObserveActivity` CAS; hot path no registry lock; no 100-connector multi-host soak |
| **PERF-SRV-03** | **Remediated** | `WriteRawAsync` awaits `WriteAsync` only; no-op `FlushAsync` removed; `_writeGate` retained; flush-count regression present |
| PERF-PROTO-01 / 02 | **Open** (not remediated in this programme) | FrameReader payload alloc; FrameWriter per-frame flush hypothesis |
| PERF-CLI-04 / 05 | **Open** | Remote→serial `ToArray`; control UTF-16 string bridge |

**Open opportunities (not started):** PERF-PROTO-01/02, PERF-CLI-04/05, optional remote-RX ArrayPool, BitBlt scroll.

**Qualification harness note:** Client suite hang on `SimulateDeviceRemoval_RaisesFault` was a **test-only** `FakeSerialPort` deadlock (`Fault`/`SignalClosed` under `port._gate` while `ReadAsync` re-entered `IsOpen`). Fixed by waking the stream outside the port lock. Not a production performance finding.

#### Consolidation suite results (Release, sequential)

| Suite | Passed | Failed | Skipped | Duration |
|---|---:|---:|---:|---|
| Protocol | **31** | 0 | 0 | ~150 ms |
| Client | **323** | 0 | 0 | ~20 s |
| Server | **1241** | 0 | 0 | ~3 m 31 s |

Clean Release build of `TechConnect.slnx`: **0 Warning(s), 0 Error(s)** (`TreatWarningsAsErrors` effective).

Release publish configuration (unchanged): Client `win-x64` self-contained non-AOT; Server `win-x64` / `linux-x64` Native AOT — see `docs/TechConnect/publish-release.md`. Full publish was **not** re-run for this consolidation (configuration + docs inspected only).

---

## Appendix A — Prior investigation points (verification)

| ID | Prior observation | Still present? | Verdict |
|---|---|---|---|
| PERF-01 | `List<byte>` per-byte Add; `RemoveRange` front removal | **Remediated** | Ring buffer (PERF-CLI-02) 2026-11-19 |
| PERF-02 | Full viewport repaint; string concat | **Remediated / fully qualified** as PERF-UI-01 | Alloc + dirty-row CPU + painted-version correctness; scroll/`YDisp` still full repaint |
| PERF-03 | Byte[] alloc per frame in FrameReader | **Yes** (PERF-PROTO-01) | Remains; Server second copy removed (PERF-SRV-01) |
| PERF-04 | Extra copy of serial RX chunks | **Partial** (PERF-CLI-01) | Local ephemeral done; remote durable retained |

## Appendix B — Key files inspected

**Protocol:** `FrameReader.cs`, `FrameWriter.cs`, `ProtocolSession.cs`, `ProtocolFrame.cs`, `ProtocolLimits.cs`, `ControlMessageSerializer.cs`, `ProtocolJsonContext.cs`, `ControlProtocol.cs`

**Client:** `SerialConnectionService.cs`, `SerialConsoleBuffer.cs`, `SerialTerminalControl.cs`, `VtTerminalHost.cs`, `MainForm.cs`, `RemoteSessionService.cs`, `RemoteOutboundBuffer.cs`, `ReconnectCoordinator.cs`, `TcpMtlsRemoteConsoleTransport.cs`, `RemoteServiceOptions.cs`

**Server:** `ConnectorListenerHostedService.cs`, `ConnectorConnection.cs`, `BoundedByteQueue.cs`, `OperatorAttachment.cs`, `OperatorAttachmentManager.cs`, `ConnectorSessionRegistry.cs`, `TechConnectCliSession.cs`, `SshServerHostedService.cs`, configuration options / `appsettings.json`

**Tests (workload models):** Protocol framing tests; Client `SerialConsoleBufferTests`, `SerialRxPipelineTests`, `SerialRemote*`, dirty-paint / ownership / ingress lifecycle; Server `SerialForwardingIntegrationTests`, `BoundedByteQueueTests`, TouchActivity / SSH flush probes

## Appendix C — Consolidation confirmation (2026-11-19)

- Production remediations for PERF-CLI-01/02/03, PERF-UI-01, PERF-SRV-01/02/03 are present in the working tree (not an investigation-only snapshot).
- Git index/history was **not** manipulated during this consolidation.
- Temporary measurement projects lived under `%TEMP%\TechConnectPerfAudit\` / `%TEMP%\TechConnect-PERF-*.txt` and are not part of the product.
- Historical section-local Client suite counts (285 / 298 / 302 / 323) are chronological snapshots from successive remediations; the consolidation qualification count supersedes them when present in § J.
)
