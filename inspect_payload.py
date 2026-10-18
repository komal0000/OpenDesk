"""Read-only PE import and bounded feature-string inventory; no target execution."""
import hashlib
import json
from pathlib import Path
import re
import struct

root = Path(__file__).resolve().parent
data = (root / "anydesk-payload.bin").read_bytes()
pe = struct.unpack_from("<I", data, 0x3C)[0]
count = struct.unpack_from("<H", data, pe + 6)[0]
optional_size = struct.unpack_from("<H", data, pe + 20)[0]
table = pe + 24 + optional_size
sections = []
for i in range(count):
    at = table + i * 40
    virtual_size, rva, raw_size, raw = struct.unpack_from("<IIII", data, at + 8)
    sections.append((rva, raw_size, raw))

def offset(rva):
    for start, size, raw in sections:
        if start <= rva < start + size:
            return raw + rva - start
    raise ValueError(f"Unmapped RVA: {rva:#x}")

def cstring(at):
    return data[at:data.index(b"\0", at)].decode("ascii", errors="replace")

imports = []
import_rva, import_size = struct.unpack_from("<II", data, pe + 24 + 104)
if import_rva:
    at = offset(import_rva)
    for _ in range(1024):
        original, _, _, name, first = struct.unpack_from("<IIIII", data, at)
        if not any((original, name, first)):
            break
        dll = cstring(offset(name))
        thunk = offset(original or first)
        for _ in range(65536):
            value = struct.unpack_from("<I", data, thunk)[0]
            if not value:
                break
            function = f"ordinal:{value & 0xFFFF}" if value & 0x80000000 else cstring(offset(value) + 2)
            imports.append({"dll": dll, "function": function})
            thunk += 4
        at += 20

patterns = re.compile(rb"(?i)(deskrt|openssl|D3D11CreateDevice|DXGI|SendInput|BitBlt|ad\.anynet\.|ad\.accept\.auth\.|ad\.cfg\.sec\.|ad\.welcome\.encryption)")
strings = [{"file_offset": m.start(), "text": m.group().decode("ascii")}
           for m in re.finditer(rb"[ -~]{6,500}", data) if patterns.search(m.group())]
result = {"sha256": hashlib.sha256(data).hexdigest(), "execution": "none",
          "imports": imports, "strings": strings,
          "limitations": ["ASCII byte-string matches are candidates, not executed behavior.",
                          "Translations may describe other platforms or unavailable features.",
                          "Imports omit APIs resolved dynamically at runtime."]}
(root / "payload-inventory.json").write_text(json.dumps(result, indent=2), encoding="utf-8")
print(json.dumps({"imports": len(imports), "candidate_strings": len(strings),
                  "dlls": sorted({i['dll'] for i in imports}),
                  "input_capture_imports": [i for i in imports if re.search('SendInput|BitBlt|Clipboard|CreateService|WSA|connect|socket', i['function'])]}, indent=2))
