"""Offline reconstruction of the embedded payload in the inspected AnyDesk build.

Does not execute the application. Constants are specific to SHA-256
dd7aa730255da936af74821fe2e90cbeec20a06f543f1fabf924d129f526c014.
"""
import hashlib
import json
import lzma
from pathlib import Path
import struct

source = Path(r"C:\Program Files (x86)\AnyDesk\AnyDesk.exe")
out = Path(__file__).resolve().parent
data = source.read_bytes()
digest = hashlib.sha256(data).hexdigest()
assert digest == "dd7aa730255da936af74821fe2e90cbeec20a06f543f1fabf924d129f526c014"
pe = struct.unpack_from("<I", data, 0x3C)[0]
count = struct.unpack_from("<H", data, pe + 6)[0]
optional_size = struct.unpack_from("<H", data, pe + 20)[0]
table = pe + 24 + optional_size
base = struct.unpack_from("<I", data, pe + 24 + 28)[0]
payload_rva = 0xDF8000 - base
offset = None
for i in range(count):
    sec = table + 40 * i
    size, rva, raw_size, raw = struct.unpack_from("<IIII", data, sec + 8)
    if rva <= payload_rva < rva + raw_size:
        offset = raw + payload_rva - rva
        assert payload_rva - rva + 0x37528A <= raw_size
        break
assert offset is not None
packed = bytearray(data[offset:offset + 0x37528A])
state = 0x3960
for i in range(len(packed)):
    state = (state * 0x19660D + 0x3C6EF35F) & 0xFFFFFFFF
    packed[i] ^= (state >> 12) & 0xFF
prop = packed[0]
lc = prop % 9
rest = prop // 9
lp, pb = rest % 5, rest // 5
dictionary = struct.unpack_from("<I", packed, 1)[0]
assert lc + lp <= 4 and pb <= 4 and dictionary <= 256 * 1024 * 1024
decoder = lzma.LZMADecompressor(format=lzma.FORMAT_RAW, filters=[{
    "id": lzma.FILTER_LZMA1, "dict_size": dictionary,
    "lc": lc, "lp": lp, "pb": pb,
}])
payload = decoder.decompress(bytes(packed[5:]), max_length=64 * 1024 * 1024)
assert len(payload) == 0x9E2E00, "Unexpected decoded size for this build's loader argument"
assert payload[:2] == b"MZ"
assert payload[struct.unpack_from("<I", payload, 0x3C)[0]:][:4] == b"PE\0\0"
destination = out / "anydesk-payload.bin"
assert not destination.exists(), "Preserve existing derived output"
destination.write_bytes(payload)
metadata = {
    "source": str(source), "source_sha256": digest,
    "packed_file_offset": offset, "packed_bytes": len(packed),
    "lzma_properties": {"lc": lc, "lp": lp, "pb": pb, "dictionary": dictionary},
    "decoder_eof": decoder.eof, "unused_bytes": len(decoder.unused_data),
    "loader_expected_bytes": 0x9E2E00,
    "derived_path": str(destination), "derived_bytes": len(payload),
    "derived_sha256": hashlib.sha256(payload).hexdigest(),
    "execution": "none",
}
(out / "payload-provenance.json").write_text(json.dumps(metadata, indent=2), encoding="utf-8")
print(json.dumps(metadata, indent=2))
