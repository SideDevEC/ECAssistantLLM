#!/usr/bin/env python3
"""Trims duplicate content trees from ECAssistant.LLM.Server nupkg.

LLamaSharp's backend .props inject native runtimes as Content items with
CopyToOutputDirectory, which NuGet pack copies into contentFiles/ and content/
root — while the ONLY functional layout is content/server/ (consumed by the
wizard and the package's .targets). This script rewrites the nupkg in place,
dropping every entry outside content/server/ (plus nuspec/build/README).
Deterministic post-pack step; keeps the zip structure otherwise intact.
"""
import sys, zipfile

def keep(name: str) -> bool:
    if name in ("[Content_Types].xml", "_rels/.rels"):
        return True
    if name.endswith(".nuspec") or name.startswith("build/") or name == "README.md":
        return True
    if name.startswith("content/server/"):
        return True
    return False

def main(path: str) -> None:
    src = zipfile.ZipFile(path)
    entries = [(i, src.read(i.filename)) for i in src.infolist() if keep(i.filename)]
    src.close()
    with zipfile.ZipFile(path, "w", zipfile.ZIP_DEFLATED, compresslevel=9) as out:
        for info, data in entries:
            ni = zipfile.ZipInfo(info.filename, date_time=info.date_time)
            ni.compress_type = zipfile.ZIP_DEFLATED
            ni.external_attr = info.external_attr
            out.writestr(ni, data)
    print(f"trimmed: {len(entries)} entries kept")

if __name__ == "__main__":
    main(sys.argv[1])
