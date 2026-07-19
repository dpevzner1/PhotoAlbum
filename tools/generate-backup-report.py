import json, os, datetime, collections

DEST = r"C:\Users\demit\Desktop\PHOTOS\2026-Backup1"
INV  = r"C:\Users\demit\AppData\Local\PhotoAlbum\phone-cache\F04470Q02Y\inventory.json"
journal_path = os.path.join(DEST, ".photoalbum-backup-journal.json")

j = json.load(open(journal_path, encoding="utf-8"))
items = j.get("Items", {})

# Inventory: itemId(path) -> metadata (size, date, isVideo, folder, name)
inv = {}
try:
    for m in json.load(open(INV, encoding="utf-8")):
        inv[m["ItemId"]] = m
except Exception as e:
    print("inv load warn:", e)

def err_explain(msg):
    if msg is None: return ""
    if "0x8007001E" in msg or "cannot read" in msg:
        return "device returned no data (0-byte read) — most likely an iCloud-optimized original not stored locally on the phone"
    if "Size mismatch" in msg:
        return "download truncated — received fewer bytes than the device reported; not saved to avoid a corrupt file"
    if "no longer connected" in msg or "disconnected" in msg:
        return "device disconnected mid-transfer"
    return msg[:140]

copied, skipped, failed = [], [], []
bytes_copied = 0
type_counts = collections.Counter()
type_bytes = collections.Counter()

for path, e in items.items():
    meta = inv.get(path, {})
    name = meta.get("Name") or (e.get("N") if e.get("S")=="copied" else path.split("\\")[-1])
    size = meta.get("SizeBytes", 0)
    ext = os.path.splitext(name)[1].lower().lstrip(".")
    status = e.get("S")
    row = dict(name=name, folder=meta.get("Folder",""), size=size, ext=ext,
               hash=e.get("H"), date=meta.get("DateTaken"), status=status, raw=e.get("N"))
    if status == "copied" and e.get("N") == "(duplicate — skipped)":
        skipped.append(row)
    elif status == "copied":
        copied.append(row); bytes_copied += size
        type_counts[ext.upper()] += 1; type_bytes[ext.upper()] += size
    elif status == "failed":
        failed.append(row)

def gb(b): return f"{b/2**30:.2f} GB"
def hz(b):
    for u in ("B","KB","MB","GB"):
        if b < 1024 or u=="GB": return f"{b:.1f} {u}"
        b/=1024

lines = []
lines.append("PHOTOALBUM — BACKUP VALIDATION REPORT")
lines.append("="*60)
lines.append(f"Device key : {j.get('DeviceKey')}")
lines.append(f"Destination: {DEST}")
lines.append(f"Started    : {j.get('StartedUtc')}")
lines.append(f"Updated    : {j.get('UpdatedUtc')}  (report generated {datetime.datetime.utcnow().isoformat()}Z)")
lines.append(f"Declared total on device: {j.get('Total'):,}")
lines.append("")
lines.append("SUMMARY")
lines.append(f"  Copied (new)     : {len(copied):,}   {gb(bytes_copied)}")
lines.append(f"  Skipped (dup)    : {len(skipped):,}")
lines.append(f"  Failed           : {len(failed):,}")
acct = len(copied)+len(skipped)+len(failed)
lines.append(f"  Accounted so far : {acct:,} / {j.get('Total'):,}  ({100*acct/max(j.get('Total',1),1):.1f}%)")
lines.append("")
lines.append("COPIED — by file type")
for t,c in type_counts.most_common():
    lines.append(f"  {t:<6} {c:>7,}   {gb(type_bytes[t])}")
lines.append("")
lines.append(f"FAILURES ({len(failed)}) — granular")
if not failed:
    lines.append("  (none)")
for r in failed:
    lines.append(f"  {r['name']:<22} | {r['folder']:<24} | expected {hz(r['size']) if r['size'] else 'unknown'} | {err_explain(r['raw'])}")
lines.append("")
lines.append("NOTES")
lines.append("  * Duplicate = identical content (BLAKE3). Same name, different content is")
lines.append("    kept and saved with an incremented suffix — never overwritten.")
lines.append("  * Failed items are NOT marked copied; re-running Back Up All re-attempts only them.")
lines.append("  * Every copied file's BLAKE3 hash is in .photoalbum-backup-journal.json.")

out = os.path.join(DEST, "BACKUP-REPORT.txt")
open(out, "w", encoding="utf-8").write("\n".join(lines))

# CSV manifest
import csv
csvp = os.path.join(DEST, "backup-manifest.csv")
with open(csvp, "w", newline="", encoding="utf-8") as f:
    w = csv.writer(f)
    w.writerow(["FileName","DeviceFolder","SizeBytes","Type","Status","BLAKE3","CaptureDate"])
    for r in copied+skipped+failed:
        st = "skipped-duplicate" if r in skipped else r["status"]
        w.writerow([r["name"], r["folder"], r["size"], r["ext"].upper(), st, r["hash"] or "", r["date"] or ""])

print("\n".join(lines[:26]))
print(f"\nWrote:\n  {out}\n  {csvp}  ({len(copied)+len(skipped)+len(failed)} rows)")
