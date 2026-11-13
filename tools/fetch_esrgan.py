import io, os, urllib.request, zipfile

URL = "https://ghproxy.net/https://github.com/xinntao/Real-ESRGAN/releases/download/v0.2.5.0/realesrgan-ncnn-vulkan-20220424-windows.zip"
OUT = r"D:\Desktop\wallerpaper c\REAL-ERGAN"
os.makedirs(OUT, exist_ok=True)


class HttpRangeFile(io.RawIOBase):
    def __init__(self, url):
        self.url = url
        req = urllib.request.Request(url, method="HEAD")
        with urllib.request.urlopen(req, timeout=60) as r:
            self.size = int(r.headers["Content-Length"])
        self.pos = 0

    def seekable(self): return True
    def readable(self): return True
    def tell(self): return self.pos

    def seek(self, off, whence=0):
        if whence == 0: self.pos = off
        elif whence == 1: self.pos += off
        elif whence == 2: self.pos = self.size + off
        return self.pos

    def readinto(self, b):
        data = self.read(len(b))
        b[:len(data)] = data
        return len(data)

    def read(self, n=-1):
        if n is None or n < 0:
            end = self.size - 1
        else:
            end = min(self.size - 1, self.pos + n - 1)
        if self.pos > end:
            return b""
        req = urllib.request.Request(self.url, headers={"Range": f"bytes={self.pos}-{end}"})
        with urllib.request.urlopen(req, timeout=120) as r:
            data = r.read()
        self.pos += len(data)
        return data


remote = HttpRangeFile(URL)
zf = zipfile.ZipFile(io.BufferedReader(remote, buffer_size=1024 * 256))
names = zf.namelist()
for n in names:
    print(n, zf.getinfo(n).file_size)

wanted = [n for n in names if not n.endswith('/') and (
    n.lower().endswith('.exe') or n.lower().endswith('.dll') or
    n.replace('\\', '/').startswith('models/'))]
print('WANTED', wanted)
for name in wanted:
    target = os.path.join(OUT, name.replace('/', os.sep).replace('\\', os.sep))
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with zf.open(name) as src, open(target, 'wb') as dst:
        while True:
            chunk = src.read(1024 * 1024)
            if not chunk:
                break
            dst.write(chunk)
    print('wrote', target, os.path.getsize(target))
