import sys

def read_id(b, p):
    if p >= len(b): return None, p
    first = b[p]
    if first == 0: return None, p + 1
    length = 1
    mask = 0x80
    while length <= 4 and not (first & mask):
        length += 1
        mask >>= 1
    if length > 4: return None, p + 1
    return int.from_bytes(b[p:p+length], 'big'), p + length

def read_size(b, p):
    if p >= len(b): return None, p
    first = b[p]
    if first == 0: return None, p + 1
    length = 1
    mask = 0x80
    while length <= 8 and not (first & mask):
        length += 1
        mask >>= 1
    if length > 8: return None, p + 1
    value = first & (mask - 1)
    for i in range(1, length):
        value = (value << 8) | b[p+i]
    unknown = value == (1 << (7*length)) - 1
    return (None if unknown else value), p + length

def children(b, start, end, wanted, depth=0):
    p = start
    while p < end:
        eid, p2 = read_id(b, p)
        if eid is None: break
        size, p3 = read_size(b, p2)
        if size is None:
            # unknown-size: consume to end
            if depth < 6:
                children(b, p3, end, wanted, depth+1)
            return
        data_start = p3
        data_end = min(end, data_start + size)
        if eid in wanted:
            return (eid, data_start, data_end)
        if eid in (0x18538067, 0x1654AE6B, 0xAE, 0xE0) and depth < 6:
            found = children(b, data_start, data_end, wanted, depth+1)
            if found: return found
        p = data_end

path = sys.argv[1]
b = open(path, 'rb').read()
found = children(b, 0, len(b), {0x23E383})
if found:
    _, s, e = found
    ns = int.from_bytes(b[s:e], 'big')
    print('defaultDurationNs', ns, 'fps', 1e9/ns if ns else 0)
else:
    print('not found')
