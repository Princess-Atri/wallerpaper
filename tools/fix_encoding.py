import sys

path = r"D:\Desktop\wallerpaper c\samples\WallerpaperShell\MainWindow.xaml.cs"
data = open(path, "rb").read()
text = data.decode("utf-8")
lines = text.splitlines(keepends=True)
out = []
bad = []
for i, line in enumerate(lines, 1):
    if not any(ord(ch) > 127 for ch in line):
        out.append(line)
        continue
    try:
        repaired = line.encode("gbk", "strict").decode("utf-8", "strict")
        out.append(repaired)
    except Exception as exc:
        out.append(line)
        bad.append((i, repr(line[:80]), str(exc)))

open(path, "w", encoding="utf-8-sig", newline="").write("".join(out))
print("lines", len(lines), "bad", len(bad))
for item in bad[:20]:
    print(item)
