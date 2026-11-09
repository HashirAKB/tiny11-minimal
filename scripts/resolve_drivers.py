#!/usr/bin/env python3
"""Resolve Windows 10/11 x64 drivers for every device in drivers/devices.json from the Microsoft Update Catalog.

For each device, the hardware-ID queries run from most specific (with Dell subsystem) to generic.
Results are grouped by package family (title without version) and the newest release of each family wins,
so a base driver comes with its Dell extension INF / software components when the catalog has them.

  resolve:  python resolve_drivers.py resolve devices.json manifest.json      (pins exact cab URLs)
  fetch:    python resolve_drivers.py fetch manifest.json <out-dir>          (downloads + expands cabs)
"""
import html, json, os, re, subprocess, sys, time, urllib.parse, urllib.request
from datetime import datetime

CATALOG = "https://www.catalog.update.microsoft.com"
UA = {"User-Agent": "Mozilla/5.0 (Windows NT 10.0; Win64; x64)"}
OK_PRODUCTS = re.compile(r"Windows 11|Windows - Client, version 2[1-9]H2|Windows 10, Vibranium|Windows 10 and later|"
                         r"Windows 10, version 1[89]0[39] and later|Windows 10, version 2004 and later", re.I)
BAD_TITLE = re.compile(r"\bARM64\b|\bx86\b|Windows Server|HoloLens|Windows 10 S only", re.I)


def get(url, data=None, tries=4):
    for i in range(tries):
        try:
            req = urllib.request.Request(url, data=data, headers=UA)
            with urllib.request.urlopen(req, timeout=90) as r:
                return r.read()
        except Exception as e:  # noqa: BLE001
            if i == tries - 1:
                raise
            time.sleep(3 * (i + 1))


def search(query):
    page = get(f"{CATALOG}/Search.aspx?q={urllib.parse.quote(query)}").decode("utf-8", "ignore")
    rows = []
    for m in re.finditer(r'<tr id="([0-9a-f-]{36})_R\d+"(.*?)</tr>', page, re.S):
        cells = [re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", "", c))).strip()
                 for c in re.findall(r"<td[^>]*>(.*?)</td>", m.group(2), re.S)]
        if len(cells) < 6:
            continue
        title, products, classification, date = cells[1], cells[2], cells[3], cells[4]
        try:
            when = datetime.strptime(date, "%m/%d/%Y")
        except ValueError:
            continue
        rows.append({"id": m.group(1), "title": title, "products": products, "class": classification, "date": when})
    return rows


def version_key(title):
    m = re.search(r"(\d+(?:\.\d+)+)\s*$", title)
    return tuple(int(x) for x in m.group(1).split(".")) if m else ()


def family(title):
    return re.sub(r"\s*-?\s*\d+(?:\.\d+)+\s*$", "", title).strip().lower()


def cab_urls(update_id):
    body = urllib.parse.urlencode({"updateIDs": json.dumps(
        [{"size": 0, "languages": "", "uidInfo": update_id, "updateID": update_id}])}).encode()
    text = get(f"{CATALOG}/DownloadDialog.aspx", data=body).decode("utf-8", "ignore")
    return re.findall(r"url = '([^']+)'", text)


def resolve(devices_path, manifest_path):
    devices = json.load(open(devices_path))
    manifest = []
    for dev in devices:
        rows, seen = [], set()
        for q in dev["queries"]:
            for r in search(q):
                if r["id"] in seen or r["date"].year < 2010:
                    continue
                if not OK_PRODUCTS.search(r["products"]) or BAD_TITLE.search(r["title"]):
                    continue
                if dev.get("prefer") and not re.search(dev["prefer"], r["title"], re.I):
                    continue
                seen.add(r["id"]); r["query"] = q; rows.append(r)
        if not rows:
            print(f"!! {dev['name']}: no Windows 10/11 driver in catalog (in-box driver will be used)")
            manifest.append({"device": dev["name"], "packages": []})
            continue
        newest = lambda rs: max(rs, key=lambda r: (r["date"], version_key(r["title"])))
        picks = [newest(rows)]
        ext = [r for r in rows if re.search(r"extension|softwarecomponent", r["title"] + " " + r["class"], re.I)]
        if ext and newest(ext)["id"] != picks[0]["id"]:
            picks.append(newest(ext))
        pkgs = []
        for r in picks:
            urls = cab_urls(r["id"])
            pkgs.append({"title": r["title"], "date": r["date"].strftime("%Y-%m-%d"), "updateId": r["id"],
                         "query": r["query"], "urls": urls})
            print(f"   {dev['name']:<30} {r['title']}  ({r['date']:%Y-%m-%d})  [{r['query']}]")
        manifest.append({"device": dev["name"], "packages": pkgs})
    json.dump(manifest, open(manifest_path, "w"), indent=2)


def expand(cab, dest):
    os.makedirs(dest, exist_ok=True)
    subprocess.run(["7z", "x", "-y", f"-o{dest}", cab], check=True, stdout=subprocess.DEVNULL)


def fetch(manifest_path, out_dir):
    manifest = json.load(open(manifest_path))
    os.makedirs(out_dir, exist_ok=True)
    cache = os.path.join(out_dir, "_cabs")
    os.makedirs(cache, exist_ok=True)
    n, done = 0, set()
    for entry in manifest:
        for pkg in entry["packages"]:
            for url in pkg["urls"]:
                if url in done:
                    print(f"shared  {entry['device']}: {pkg['title']} (already fetched)")
                    continue
                done.add(url)
                name = url.rsplit("/", 1)[-1]
                cab = os.path.join(cache, name)
                if not os.path.exists(cab):
                    with open(cab + ".tmp", "wb") as f:
                        f.write(get(url))
                    os.replace(cab + ".tmp", cab)
                slug = re.sub(r"[^A-Za-z0-9._-]+", "_", f"{entry['device']}__{pkg['title']}")[:120]
                expand(cab, os.path.join(out_dir, slug))
                n += 1
                print(f"fetched {entry['device']}: {pkg['title']}")
    print(f"{n} driver package(s) expanded into {out_dir}")


if __name__ == "__main__":
    {"resolve": resolve, "fetch": fetch}[sys.argv[1]](*sys.argv[2:])
