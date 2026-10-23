#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""校验 make-winget-manifest.ps1 产出的 winget 清单：官方 schema + winget-pkgs 硬规则。

用法：
    # 首次：把官方 1.6.0 schema 拉到 ./schema
    python validate-manifests.py --manifest-dir dist/winget --zip dist/MCLauncher-0.1.2-win-x64.zip --fetch-schemas

    # 之后
    python validate-manifests.py --manifest-dir dist/winget --zip dist/MCLauncher-0.1.2-win-x64.zip

依赖：pyyaml、jsonschema（pip install --user pyyaml jsonschema）
退出码：0 全过；1 有失败项（失败项会逐条打印）。
"""
import argparse
import json
import os
import re
import sys
import urllib.request
import zipfile

try:
    import yaml
    from jsonschema import Draft7Validator
except ImportError as exc:  # pragma: no cover
    sys.exit('缺少依赖：%s（pip install --user pyyaml jsonschema）' % exc)

SCHEMA_BASE = 'https://aka.ms/winget-manifest.%s.1.6.0.schema.json'
SCHEMA_KINDS = ('version', 'installer', 'locale', 'defaultLocale')
results = []


def check(name, ok, detail=''):
    results.append((name, bool(ok), detail))
    print(('PASS  ' if ok else 'FAIL  ') + name + (('  -> ' + detail) if detail else ''))


def fetch_schemas(schema_dir):
    os.makedirs(schema_dir, exist_ok=True)
    for kind in SCHEMA_KINDS:
        dst = os.path.join(schema_dir, kind + '.schema.json')
        if os.path.exists(dst) and os.path.getsize(dst) > 1024:
            print('已存在 %s' % dst)
            continue
        url = SCHEMA_BASE % kind
        print('下载 %s' % url)
        with urllib.request.urlopen(url, timeout=60) as resp:
            body = resp.read()
        if not body.lstrip().startswith(b'{'):
            sys.exit('%s 返回的不是 JSON（%d 字节），aka.ms 的短名可能已失效' % (url, len(body)))
        with open(dst, 'wb') as fh:
            fh.write(body)


def load_schema(schema_dir, kind):
    path = os.path.join(schema_dir, kind + '.schema.json')
    with open(path, 'rb') as fh:
        return json.load(fh)


def schema_kind(doc):
    return {'version': 'version', 'installer': 'installer', 'locale': 'locale'}.get(doc.get('ManifestType'))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--manifest-dir', required=True, help='make-winget-manifest.ps1 的 -OutputDir')
    ap.add_argument('--zip', required=True, help='InstallerUrl 对应的那个 zip（用来核对 sha256 与嵌套文件）')
    ap.add_argument('--schema-dir', default=os.path.join(os.path.dirname(os.path.abspath(__file__)), 'schema'))
    ap.add_argument('--fetch-schemas', action='store_true', help='先从 aka.ms 下载官方 schema')
    args = ap.parse_args()

    if args.fetch_schemas:
        fetch_schemas(args.schema_dir)
    if not os.path.isdir(args.schema_dir):
        sys.exit('schema 目录不存在：%s（加 --fetch-schemas）' % args.schema_dir)

    root = os.path.abspath(args.manifest_dir)
    zip_path = os.path.abspath(args.zip)
    if not os.path.isfile(zip_path):
        sys.exit('zip 不存在：%s' % zip_path)

    files = sorted(
        os.path.join(dp, fn) for dp, _dn, fns in os.walk(root) for fn in fns
    )
    check('目录里恰好 3 个 yaml', len(files) == 3, '%d 个: %s' % (len(files), [os.path.basename(f) for f in files]))
    if len(files) != 3:
        return 1

    docs = {}
    for path in files:
        base = os.path.basename(path)
        raw = open(path, 'rb').read()
        check('%s: 无 BOM' % base, not raw.startswith(b'\xef\xbb\xbf'))
        check('%s: 纯 LF（无 CR）' % base, b'\r' not in raw, '出现 %d 个 \\r' % raw.count(b'\r'))
        try:
            text = raw.decode('utf-8')
        except UnicodeDecodeError as exc:
            check('%s: UTF-8 可解码' % base, False, str(exc))
            continue
        doc = yaml.safe_load(text)
        docs[base] = doc
        kind = schema_kind(doc)
        check('%s: ManifestType 可识别' % base, kind is not None, str(doc.get('ManifestType')))
        if kind:
            errs = sorted(Draft7Validator(load_schema(args.schema_dir, kind)).iter_errors(doc),
                          key=lambda e: list(e.path))
            detail = '; '.join('/'.join(str(p) for p in e.path) + ': ' + e.message for e in errs[:5])
            check('%s: 通过官方 %s.1.6.0 schema' % (base, kind), not errs, detail)

    ids = {d.get('PackageIdentifier') for d in docs.values()}
    vers = {d.get('PackageVersion') for d in docs.values()}
    pid = next(iter(ids)) or ''
    ver = next(iter(vers)) or ''
    check('三个文件的 PackageIdentifier 一致', len(ids) == 1, str(ids))
    check('三个文件的 PackageVersion 一致', len(vers) == 1, str(vers))
    check('PackageIdentifier 段数 >= 2', pid.count('.') >= 1, pid)
    check('PackageIdentifier 每段合法', all(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9_\-]*', s) for s in pid.split('.')), pid)
    check('PackageIdentifier 长度 <= 128', len(pid) <= 128, str(len(pid)))
    check('PackageIdentifier 无空格/路径字符', not re.search(r'[\s\\/:*?"<>|]', pid), pid)
    check('PackageVersion 形如 x.y.z', bool(re.fullmatch(r'\d+\.\d+\.\d+', ver)), ver)

    locale_names = [b for b in docs if b.startswith('%s.locale.' % pid)]
    check('存在 %s.yaml' % pid, '%s.yaml' % pid in docs)
    check('存在 %s.installer.yaml' % pid, '%s.installer.yaml' % pid in docs)
    check('存在 %s.locale.<locale>.yaml' % pid, len(locale_names) == 1, str(locale_names))
    if len(locale_names) != 1:
        return 1

    vdoc = docs['%s.yaml' % pid]
    idoc = docs['%s.installer.yaml' % pid]
    ldoc = docs[locale_names[0]]

    check('version: DefaultLocale 非空', bool(vdoc.get('DefaultLocale')), str(vdoc.get('DefaultLocale')))
    check('version: ManifestVersion == 1.6.0', vdoc.get('ManifestVersion') == '1.6.0', str(vdoc.get('ManifestVersion')))

    check('installer: InstallerType == zip', idoc.get('InstallerType') == 'zip', str(idoc.get('InstallerType')))
    check('installer: NestedInstallerType == portable',
          idoc.get('NestedInstallerType') == 'portable', str(idoc.get('NestedInstallerType')))
    installers = idoc.get('Installers') or []
    check('installer: Installers 恰好 1 项', len(installers) == 1, str(len(installers)))
    if installers:
        inst = installers[0]
        sha = inst.get('InstallerSha256') or ''
        check('installer: InstallerSha256 为 64 位大写 hex', bool(re.fullmatch(r'[0-9A-F]{64}', sha)), sha)
        url = inst.get('InstallerUrl') or ''
        check('installer: InstallerUrl 指向 releases/download/v<ver>/',
              url.startswith('https://github.com/') and '/releases/download/v%s/' % ver in url, url)
        check('installer: Architecture == x64', inst.get('Architecture') == 'x64', str(inst.get('Architecture')))
        check('installer: URL 文件名与本地 zip 一致',
              url.rsplit('/', 1)[-1] == os.path.basename(zip_path), url.rsplit('/', 1)[-1])

    nested = idoc.get('NestedInstallerFiles') or []
    check('installer: NestedInstallerFiles 非空', len(nested) > 0, str(nested))
    with zipfile.ZipFile(zip_path) as zf:
        entries = {e.filename.replace('\\', '/') for e in zf.infolist() if not e.is_dir()}
    for item in nested:
        rel = (item.get('RelativeFilePath') or '').replace('\\', '/')
        check('嵌套文件 %s 存在于 zip' % rel, rel in entries)
        check('嵌套文件 %s 是相对路径' % rel, bool(rel) and not os.path.isabs(rel) and '..' not in rel, rel)
        alias = item.get('PortableCommandAlias')
        if alias:
            check('PortableCommandAlias 合法：%s' % alias, bool(re.fullmatch(r'[A-Za-z0-9][A-Za-z0-9\-_]*', alias)), alias)

    check('locale: PackageLocale 与文件名后缀一致',
          locale_names[0].endswith('.locale.%s.yaml' % ldoc.get('PackageLocale')), str(ldoc.get('PackageLocale')))
    check('locale: DefaultLocale == PackageLocale',
          vdoc.get('DefaultLocale') == ldoc.get('PackageLocale'),
          '%s vs %s' % (vdoc.get('DefaultLocale'), ldoc.get('PackageLocale')))
    for key in ('Publisher', 'PackageName', 'ShortDescription', 'License', 'LicenseUrl', 'PackageUrl', 'Moniker'):
        check('locale: %s 非空' % key, bool(ldoc.get(key)), str(ldoc.get(key))[:60])
    check('locale: ShortDescription <= 256', len(ldoc.get('ShortDescription') or '') <= 256)
    check('locale: Tags 数量 1..16', 1 <= len(ldoc.get('Tags') or []) <= 16, str(len(ldoc.get('Tags') or [])))

    rel_dir = os.path.relpath(os.path.dirname(files[0]), root).replace('\\', '/')
    want = '%s/%s/%s/%s' % (pid.split('.')[0].lower()[0], pid.split('.')[0], '.'.join(pid.split('.')[1:]), ver)
    check('目录布局 == manifests/<首字母小写>/<Publisher>/<Package>/<Version>', rel_dir == want,
          '实际 %s / 期望 %s' % (rel_dir, want))

    bad = [r for r in results if not r[1]]
    print('\n==== 合计 %d 项，FAIL %d 项 ====' % (len(results), len(bad)))
    for name, _ok, detail in bad:
        print('  FAIL: %s %s' % (name, detail))
    return 1 if bad else 0


if __name__ == '__main__':
    sys.exit(main())
