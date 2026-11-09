#!/usr/bin/env python3
"""Подготавливает подписанный канал обновления; ничего не публикует в GitHub."""
import argparse
import base64
import hashlib
import json
import re
import subprocess
import tempfile
from pathlib import Path

REPOSITORY = 'kazievnadir-max/blackeagle-release'
CERTIFICATE = '990b9d5a3fafd367facde05cceada668ba2f6210da84d2d07e5a2cfa820ce383'
ROOT = Path(__file__).resolve().parents[1]


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--apk', type=Path, required=True)
    parser.add_argument('--key', type=Path, required=True, help='RSA private PEM outside the repository')
    parser.add_argument('--build-tools', type=Path, required=True)
    parser.add_argument('--notes', type=Path, required=True, help='UTF-8 plain text, up to 4000 characters')
    parser.add_argument('--output', type=Path, required=True)
    args = parser.parse_args()
    key = args.key.resolve()
    if key.is_relative_to(ROOT):
        parser.error('Private signing key must be stored outside the repository')
    verified = subprocess.check_output([str(args.build_tools / 'apksigner'), 'verify', '--verbose', '--print-certs', str(args.apk)], text=True)
    certificates = re.findall(r'Signer #\d+ certificate SHA-256 digest: ([a-fA-F0-9]+)', verified)
    if [value.lower() for value in certificates] != [CERTIFICATE]:
        parser.error('APK must be signed with the permanent Android release key')
    badging = subprocess.check_output([str(args.build_tools / 'aapt'), 'dump', 'badging', str(args.apk)], text=True)
    package = re.search(r"package: name='([^']+)' versionCode='(\d+)' versionName='([^']+)'", badging)
    native = re.search(r"native-code: (.+)", badging)
    if not package or package[1] != 'com.blackeaglevpn.client' or not native:
        parser.error('Unexpected APK package or missing native ABI')
    abis = re.findall(r"'([^']+)'", native[1])
    architecture = {'arm64-v8a': 'arm64', 'x86_64': 'x64'}.get(abis[0]) if len(abis) == 1 else None
    if not architecture:
        parser.error('Expected one supported APK ABI: arm64-v8a or x86_64')
    version = package[3]
    if not re.fullmatch(r'\d+\.\d+\.\d+', version):
        parser.error('Expected stable version X.Y.Z')
    size = args.apk.stat().st_size
    if not 0 < size <= 300 * 1024 * 1024:
        parser.error('APK exceeds the client size limit')
    notes = args.notes.read_text(encoding='utf-8').strip()
    if len(notes) > 4000:
        parser.error('Release notes exceed 4000 characters')
    asset = f'BlackEagleVPN-{version}-{architecture}.apk'
    url = f'https://github.com/{REPOSITORY}/releases/download/android-v{version}/{asset}'
    with args.apk.open('rb') as apk:
        checksum = hashlib.file_digest(apk, 'sha256').hexdigest()
    manifest = dict(schema=1, platform='android', architecture=architecture, packageId=package[1],
                    versionCode=int(package[2]), version=version, url=url, size=size, sha256=checksum, notes=notes)
    payload = json.dumps(manifest, ensure_ascii=False, separators=(',', ':')).encode()
    public_key = ROOT / 'clients/android/src/BlackEagle.Android/Updates/update-public.pem'
    with tempfile.TemporaryDirectory() as temp:
        data = Path(temp) / 'payload'
        signature = Path(temp) / 'signature'
        data.write_bytes(payload)
        subprocess.run(['openssl', 'dgst', '-sha256', '-sign', str(key), '-sigopt', 'rsa_padding_mode:pss',
                        '-sigopt', 'rsa_pss_saltlen:32', '-out', str(signature), str(data)], check=True)
        subprocess.run(['openssl', 'dgst', '-sha256', '-verify', str(public_key), '-sigopt', 'rsa_padding_mode:pss',
                        '-sigopt', 'rsa_pss_saltlen:32', '-signature', str(signature), str(data)], check=True)
        envelope = dict(payload=base64.b64encode(payload).decode(), signature=base64.b64encode(signature.read_bytes()).decode())
    args.output.mkdir(parents=True, exist_ok=True)
    target = args.output / f'android-{architecture}.json'
    target.write_text(json.dumps(envelope, indent=2) + '\n', encoding='utf-8')
    (args.output / 'SHA256SUMS.txt').write_text(f'{checksum}  {asset}\n', encoding='utf-8')
    print(f'Manifest: {target}\nTag: android-v{version}\nAsset name: {asset}\nUpload APK before publishing channels/android-{architecture}.json')


if __name__ == '__main__':
    main()
