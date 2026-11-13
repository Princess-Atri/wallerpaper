import json, os, shutil

cfg = os.path.join(os.environ["LOCALAPPDATA"], "Wallerpaper", "shell-config.json")
bak = os.path.join(os.environ["TEMP"], "seq-config-backup.json")
shutil.copy2(cfg, bak)
d = json.load(open(cfg, encoding="utf-8-sig"))
first = os.path.join(os.environ["USERPROFILE"], "Videos", "Wallerpaper", "demo.mp4")
second = r"d:\Downloads\CH0336_home_Idle_01(1).webm"
item = {
    "id": "seqtest00001",
    "title": "组合测试",
    "kind": "Sequence",
    "path": first,
    "secondPath": second,
    "folder": "",
    "thumbnailPath": "",
    "volume": 0,
    "speed": 1.0,
    "muted": True,
    "shaderParameters": {},
    "missing": False,
}
d["wallpapers"] = [x for x in d.get("wallpapers", []) if x.get("id") != "seqtest00001"]
d["wallpapers"].append(item)
d["monitors"] = {"\\\\.\\DISPLAY1": {"wallpaperId": "seqtest00001", "fps": 60, "enabled": True}}
json.dump(d, open(cfg, "w", encoding="utf-8"), ensure_ascii=False, indent=2)
print("sequence config written")
