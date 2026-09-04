"""Verify the icon embedded in myrouter.exe matches myrouter.ico (using pefile)."""
import struct
import pefile

ICO = r"C:\code\C#\myrouter\myrouter.ico"
EXE = r"C:\code\C#\myrouter\bin\Release\net10.0-windows\myrouter.exe"

# --- parse ico ---
with open(ICO, "rb") as f:
    ico = f.read()
count = struct.unpack_from("<H", ico, 4)[0]
ico_images = {}
for i in range(count):
    w = ico[6 + 16 * i]
    h = ico[7 + 16 * i]
    size = struct.unpack_from("<I", ico, 6 + 16 * i + 8)[0]
    off = struct.unpack_from("<I", ico, 6 + 16 * i + 12)[0]
    ico_images[(w, h)] = ico[off:off + size]
    print(f"ico: {'256' if w == 0 else w}x{'256' if h == 0 else h}  bytes={size}")

# --- extract icons from exe ---
pe = pefile.PE(EXE)
if not hasattr(pe, "DIRECTORY_ENTRY_RESOURCE"):
    print("NO resource directory in exe — icon NOT embedded!")
    raise SystemExit(1)

icon_res = {}  # (w,h) -> bytes
for entry in pe.DIRECTORY_ENTRY_RESOURCE.entries:
    if entry.id != pefile.RESOURCE_TYPE["RT_ICON"]:
        continue
    for sub in entry.directory.entries:
        for leaf in sub.directory.entries:
            d = leaf.data.struct
            icon_res[sub.id] = pe.get_data(d.OffsetToData, d.Size)

group_entry = None
for entry in pe.DIRECTORY_ENTRY_RESOURCE.entries:
    if entry.id == pefile.RESOURCE_TYPE["RT_GROUP_ICON"]:
        group_entry = entry
        break
if group_entry is None:
    print("NO RT_GROUP_ICON in exe")
    raise SystemExit(1)

for sub in group_entry.directory.entries:
    for leaf in sub.directory.entries:
        d = leaf.data.struct
        raw = pe.get_data(d.OffsetToData, d.Size)
        g_count = struct.unpack_from("<H", raw, 4)[0]
        print(f"\nPE group icon ({sub.id}): {g_count} images")
        pe_images = []
        for i in range(g_count):
            e = 6 + i * 14
            w, h = raw[e], raw[e + 1]
            icon_id = struct.unpack_from("<H", raw, e + 12)[0]
            if icon_id not in icon_res:
                print(f"  id {icon_id} not found in RT_ICON!")
                continue
            d = icon_res[icon_id]
            pe_images.append(((w, h), d))
            print(f"  {'256' if w == 0 else w}x{'256' if h == 0 else h}  bytes={len(d)}")
        print("comparison:")
        all_match = True
        for (w, h), d in pe_images:
            if (w, h) in ico_images:
                m = d == ico_images[(w, h)]
                print(f"  {w}x{h}: {'MATCH' if m else 'DIFFER'}")
                all_match &= m
            else:
                print(f"  {w}x{h}: no counterpart in ico")
                all_match = False
        print("\nRESULT:", "ALL MATCH — new icon embedded in exe" if all_match else "MISMATCH")
