#!/usr/bin/env python3
"""
Lightweight post-build helper: given a publish directory and version, produce zip artifact.
"""
import sys
import os
import zipfile

def make_zip(src_dir, out_path):
    with zipfile.ZipFile(out_path, 'w', compression=zipfile.ZIP_DEFLATED) as zf:
        for root, dirs, files in os.walk(src_dir):
            for f in files:
                full = os.path.join(root, f)
                rel = os.path.relpath(full, src_dir)
                zf.write(full, rel)

def main():
    if len(sys.argv) < 4:
        print("Usage: post-build.py <publish_dir> <artifact_prefix> <version> [arch]")
        return 2
    publish_dir = sys.argv[1]
    artifact_prefix = sys.argv[2]
    version = sys.argv[3]
    arch = sys.argv[4] if len(sys.argv) >=5 else 'win-x64'

    if not os.path.isdir(publish_dir):
        print(f"Publish dir not found: {publish_dir}")
        return 2

    out_name = f"{artifact_prefix}_{version}_{arch}.zip"
    out_path = os.path.abspath(out_name)
    print(f"Creating {out_path} from {publish_dir}")
    make_zip(publish_dir, out_path)
    print("Done")
    return 0

if __name__ == '__main__':
    sys.exit(main())
#!/usr/bin/env python3
"""post-build helper: determine version and package published artifacts.

Usage:
  python scripts/post-build.py -publishDir ./artifacts/win-x64 -artifactPrefix ToastCloser -arch win-x64
"""
import argparse
import os
import subprocess
import sys
import shutil
from zipfile import ZipFile


def get_version():
    # Prefer env override
    v = os.environ.get('RELEASE_VERSION') or os.environ.get('GITHUB_REF')
    if v:
        # strip refs/tags/
        v = v.split('/')[-1]
        if v.startswith('v'):
            return v[1:]
        return v
    # Fallback to git tag
    try:
        out = subprocess.check_output(['git', 'describe', '--tags', '--abbrev=0'], stderr=subprocess.DEVNULL)
        tag = out.decode().strip()
        if tag.startswith('v'):
            return tag[1:]
        return tag
    except Exception:
        return '0.0.0'


def package(publish_dir, artifact_prefix, arch):
    version = get_version()
    zip_name = f"{artifact_prefix}_{version}_{arch}.zip"
    zip_path = os.path.join(os.getcwd(), 'artifacts', zip_name)
    os.makedirs(os.path.dirname(zip_path), exist_ok=True)

    print(f"Packaging {publish_dir} -> {zip_path}")
    with ZipFile(zip_path, 'w') as z:
        for root, dirs, files in os.walk(publish_dir):
            for f in files:
                full = os.path.join(root, f)
                arc = os.path.relpath(full, publish_dir)
                z.write(full, arc)
    print(f"Created {zip_path}")
    return zip_path, version


def main():
    p = argparse.ArgumentParser()
    p.add_argument('-publishDir', required=True)
    p.add_argument('-artifactPrefix', required=True)
    p.add_argument('-arch', required=True)
    args = p.parse_args()

    publish_dir = args.publishDir
    artifact_prefix = args.artifactPrefix
    arch = args.arch

    if not os.path.isdir(publish_dir):
        print(f'Publish directory not found: {publish_dir}', file=sys.stderr)
        sys.exit(2)

    zip_path, version = package(publish_dir, artifact_prefix, arch)
    print(version)


if __name__ == '__main__':
    main()
