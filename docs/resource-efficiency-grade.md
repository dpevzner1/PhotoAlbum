# Resource Utilization Grade — Phone View Rendering Path

Code-level audit of memory, CPU, and GPU efficiency for the phone
inventory/preview path, graded by both advisory engines. Per the APlus engine's
directive, findings are **evidence-based** (specific code paths + reasoning),
not assertions. Capture method: static code review of the render/decode/paging
hot paths; no runtime profiler was attached (recommended as a follow-up
baseline).

## Scope (hot paths reviewed)
- `PhoneItemVm.DecodeFrozen` / `SetThumbnail` — thumbnail decode
- `PhoneViewModel.ApplyTypeFilter` / `LoadPageThumbnailsAsync` — paging + load
- `PhoneView.xaml` thumbnail `Image` — GPU render settings

## GPU utilization

| Aspect | Finding | Grade |
|---|---|---|
| Compositing | WPF renders via Direct3D by default — all tiles/scroll/animation are GPU-composited. No opt-in needed. | ✅ A |
| Scaling cost | Thumbnails now set `RenderOptions.BitmapScalingMode=LowQuality` (bilinear) — materially cheaper GPU scaling than the default high-quality, imperceptible on 160px decorative tiles. | ✅ A (was B) |
| Bitmap reuse | `RenderOptions.CachingHint=Cache` + `Freeze()` — frozen bitmaps hand straight to the render thread with no clone. | ✅ A |
| **Verdict** | GPU is used to its practical ceiling for a 2D thumbnail grid. **There is no compute workload here a GPU could accelerate further** — grid rendering is not compute-bound; RAM and I/O are the real costs (addressed below). Adding GPU compute would be effort spent where nothing is bottlenecked. | **A** |

## Memory utilization

| Aspect | Finding | Grade |
|---|---|---|
| Decode size | `DecodePixelWidth=160` caps decode to tile size — a 4000px original never inflates RAM. | ✅ A |
| Realized tiles | Paging renders exactly one 500-item page → ~51 MB decoded bitmaps max, flat at any library size. | ✅ A |
| Off-page bitmaps | **DEFECT FOUND & FIXED:** decoded bitmaps for previously-viewed pages were retained in `_allItems` forever (paging all 121 pages ≈ 3 GB). Now released on page change (`_renderedPage` → `Thumbnail=null`); disk cache reloads instantly. | ✅ A (was **D**) |
| Inventory model | 60k lightweight VMs (~12 MB), no bitmap until page-rendered. | ✅ A |
| **Verdict** | After the off-page release fix, RAM is bounded and flat regardless of library size. | **A** (was C pre-fix) |

## CPU utilization

| Aspect | Finding | Grade |
|---|---|---|
| Decode thread | **IMPROVED:** thumbnail JPEG decode moved off the UI thread (`Task.Run` → frozen bitmap → assign on UI). Removes page-load micro-stutter. | ✅ A (was B) |
| Load bounding | Per-page load, cancelled on navigation — at most one page of work in flight (backpressure). | ✅ A |
| Enumeration | Bulk `MediaFileInfo` walk, not per-file round-trips; dedup via O(1) HashSet. | ✅ A |
| Hashing/copy | BLAKE3 in the Rust core (SIMD, native) — off the .NET heap entirely. | ✅ A |
| **Verdict** | CPU work is bounded, backgrounded where it matters, and native for the heavy lifting. | **A** |

## Engine grades

- **DevOps engine (bounded buffers / lazy loading / backpressure):** PASS —
  bounded rendering, per-page cancellable load, disk-cache backpressure, native
  hashing. The one gap (unbounded off-page bitmap retention) is now closed.
- **APlus engine (baseline + evidence):** findings are code-anchored; the
  recommended next step is a **runtime profiler baseline** (dotnet-counters /
  VMMap / GPU view) to convert these static grades into measured numbers.

## Honest conclusion on GPU
"Use the GPU to the fullest" here means **let WPF composite on the GPU (done)
and minimize the work handed to it (scaling mode, frozen bitmaps, bounded tile
count) — not add a compute workload.** A thumbnail grid has no
GPU-accelerable computation; the levers that actually move memory and CPU are
paging, off-page release, disk caching, and off-thread decode — all now in
place. Overall: **A**, with a profiler baseline recommended to make it
measured rather than reasoned.
