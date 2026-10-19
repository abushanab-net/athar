"""Merge new UI strings into the resx files. Each *.tsv here has lines: <set>\t<key>\t<English>\t<Arabic>, where <set> is
`shared` (Resources/SharedResource) or `wiki` (Components/Wiki/WikiText). Existing keys are left alone. Run from the repo root."""
import glob, html, pathlib, re, sys

SETS = {"shared": "src/Athar.Web/Resources/SharedResource", "wiki": "src/Athar.Web/Components/Wiki/WikiText"}
added = {}
for tsv in sorted(glob.glob(".impeccable/i18n/*.tsv")):
    for n, line in enumerate(pathlib.Path(tsv).read_text(encoding="utf-8").splitlines(), 1):
        if not line.strip() or line.startswith("#"): continue
        parts = line.split("\t")
        if len(parts) != 4 or parts[0] not in SETS: sys.exit(f"{tsv}:{n}: expected set<TAB>key<TAB>en<TAB>ar")
        s, key, en, ar = parts
        for suffix, value in (("", en), (".ar", ar)):
            path = pathlib.Path(SETS[s] + suffix + ".resx")
            raw = path.read_bytes(); bom = raw.startswith(b"\xef\xbb\xbf")
            text = raw.decode("utf-8-sig")
            if re.search(rf'<data name="{re.escape(key)}"', text): continue
            entry = f'  <data name="{key}" xml:space="preserve"><value>{html.escape(value, quote=False)}</value></data>' + ('\r\n' if '\r\n' in text else '\n')
            text = text.replace("</root>", entry + "</root>")
            path.write_text(text, encoding="utf-8-sig" if bom else "utf-8", newline="")
            added[str(path)] = added.get(str(path), 0) + 1
print(added or "nothing new")
