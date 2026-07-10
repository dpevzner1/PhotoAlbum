# Phone Feature — Industry Validation & DevOps Engine Grade

Validation of the iPhone connect/backup implementation against what shipping
commercial tools (iMazing, CopyTrans) document publicly, and the DevOps
engine's grading against its standing directives. Recorded 2026-07.

## Industry comparison (public forum/vendor documentation)

| Practice | Market leaders (iMazing / CopyTrans) | PhotoAlbum |
|---|---|---|
| iCloud-optimized photos | iMazing: optimized photos "cannot be transferred because stored in iCloud"; official remedy is enabling **Download and Keep Originals** first. CopyTrans advises the same toggle. | Same guidance in-app and in docs — plus we still attempt the transfer with per-item retries where iMazing errors per photo |
| Slow USB transfers | Community reports hours for hundreds of photos; iPhones are USB2-class over MTP | Live scan/backup metrics, resumable journal, per-item retry — built for hours-long transfers |
| Library size vs device capacity | Widely-reported user confusion; vendors distinguish "library" vs "device" | Status line says "library size"; inline explanation appears when total exceeds capacity; post-scan histogram in run log |
| Trust / driver first-line issues | Every vendor's troubleshooting starts with "install iTunes, unlock, tap Trust" (manual) | Automated: `PhoneDiagnosticsService` on every connect, winget driver bootstrap, elevated service-start action, guided status text |

**Position: at parity with commercial tools' documented behaviour; ahead on
automated diagnostics and journaled resume.**

## DevOps engine grade (standing directives)

| Directive | Grade | Evidence |
|---|---|---|
| Strict error/crash handling + logging | ✅ Pass | Retry w/ backoff, reconnect wait, health one-liners, post-scan histograms |
| Durable state / rollback path | ✅ Pass | Per-file journal, atomic renames, orphan-.part sweep, incremental hash index |
| Least privilege | ✅ Pass | Read-only device access; UAC only on explicit user action; user-writable defaults |
| Honest validation limits | ✅ Pass | MTP no-source-hash limit and iCloud size semantics documented, not hidden |
| Deployment / ownership / validation | 🟡 Partial | Manual matrix exercised on real hardware; **mock `IDeviceService` for CI is pending** |
| UX expectations before code | ✅ Pass | Feasibility doc → design → implementation; progress visible at every stage |

**Verdict: correct path forward.** One tracked gap: CI-testable mock device service.

## Sources

- iMazing — photo stored in iCloud (cannot transfer; enable Keep Originals):
  https://support.imazing.com/hc/en-us/articles/115001257454-Photos-The-photo-x-cannot-be-transferred-because-it-is-stored-in-iCloud
- iMazing — photo transfer guide: https://imazing.com/guides/how-to-manage-and-transfer-photos-between-iphone-ipad-and-computer
- CopyTrans — how iCloud Photos affects transfers: https://www.copytrans.net/support/how-icloud-photo-library-can-influence-copytrans-photo-performance/
- CopyTrans Cloudly (originals direct from iCloud): https://www.copytrans.net/copytranscloudly/
- Apple Community — slow iOS photo imports: https://discussions.apple.com/thread/255206486
- Apple — iCloud Photos / Optimize Storage semantics: https://support.apple.com/en-mt/108782

## Related docs

- [iphone-backup-feasibility.md](iphone-backup-feasibility.md) — original research & design
- [phone-backup-resilience.md](phone-backup-resilience.md) — interrupt-protection design
- [phone-connectivity-remediation.md](phone-connectivity-remediation.md) — real-world bring-up log (incl. §5 iCloud size)
- [apple-driver-install.md](apple-driver-install.md) — driver bootstrap commands
