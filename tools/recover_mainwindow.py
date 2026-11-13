from pathlib import Path

src = Path(r"D:\Desktop\wallerpaper c\artifacts\decompiled\Wallerpaper.Shell\MainWindow.cs")
dst = Path(r"D:\Desktop\wallerpaper c\samples\WallerpaperShell\MainWindow.recovered.cs")
lines = src.read_text(encoding="utf-8-sig").splitlines()
body = lines[378:1416]  # _config through SetStatus, before generated InitializeComponent

# Drop generated XAML control fields and connector bookkeeping.
kept = []
skip = False
for line in body:
    if line.strip().startswith('[GeneratedCode("Microsoft.UI.Xaml.Markup.Compiler"'):
        skip = True
        continue
    if skip:
        if line.strip().startswith("private IMainWindow_Bindings Bindings;"):
            skip = False
        continue
    kept.append(line)

header = '''using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media.Imaging;
using System.Diagnostics;
using Wallerpaper.Diagnostics;
using Wallerpaper.Shell.Models;
using Wallerpaper.Shell.Services;
using Wallerpaper.Shell.Views;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace Wallerpaper.Shell;

public sealed partial class MainWindow : Window
{
'''
# Insert our new resource fields after _refreshToken.
out = []
for line in kept:
    out.append(line)
    if line.strip() == "private int _refreshToken;":
        out.append("    private DataTemplate? _defaultGridTemplate;")
        out.append("    private ItemsPanelTemplate? _defaultGridPanel;")

text = header + "\n".join(out) + "\n}\n"
dst.write_text(text, encoding="utf-8-sig")
print("wrote", dst, "lines", text.count("\n"))
