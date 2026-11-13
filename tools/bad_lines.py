p = r"D:\Desktop\wallerpaper c\samples\WallerpaperShell\MainWindow.xaml.cs"
lines = open(p, encoding="utf-8-sig").read().splitlines()
bad = []
for i, line in enumerate(lines, 1):
    if not any(ord(c) > 127 for c in line):
        continue
    if "\ue000" <= max(line, default="\0") <= "\uf8ff" or "\ufffd" in line or "?" in line:
        bad.append((i, line))
with open(r"D:\Desktop\wallerpaper c\artifacts\bad_lines.txt", "w", encoding="utf-8") as f:
    for i, line in bad:
        f.write(f"{i}: {line!r}\n")
print(len(bad))
