#!/usr/bin/env python3
"""sync_version.py - 同步项目版本号到所有相关文件

用法: python sync_version.py <version>
例如: python sync_version.py 1.3.0

同步目标:
- CHANGELOG.md (首个 ## [X.X.X] 行)
- SettingsPanel.jsx (<p>Douzhanzhe Console vX.X.X</p>)
- douzhanzhe-setup.iss (注释行 + #define MyAppVersion)
- package.json ("version" 字段)
"""
import re
import sys
from pathlib import Path

def sync_version(version: str, root: Path):
    """同步版本号到所有相关文件"""
    print(f"[version] 同步版本号: {version} ...")
    
    # CHANGELOG.md
    changelog = root / "CHANGELOG.md"
    if changelog.exists():
        text = changelog.read_text(encoding="utf-8")
        text = re.sub(
            r'(## \[)\d+\.\d+\.\d+(\] — \d{4}-\d{2}-\d{2})',
            rf'\g<1>{version}\g<2>',
            text,
            count=1
        )
        changelog.write_text(text, encoding="utf-8")
        print(f"  ✓ CHANGELOG.md")
    
    # SettingsPanel.jsx
    settings = root / "src" / "components" / "panels" / "SettingsPanel.jsx"
    if settings.exists():
        text = settings.read_text(encoding="utf-8")
        text = re.sub(
            r'(<p>Douzhanzhe Console v)\d+\.\d+\.\d+(</p>)',
            rf'\g<1>{version}\g<2>',
            text
        )
        settings.write_text(text, encoding="utf-8")
        print(f"  ✓ SettingsPanel.jsx")
    
    # douzhanzhe-setup.iss
    iss = Path(__file__).parent / "douzhanzhe-setup.iss"
    if iss.exists():
        text = iss.read_text(encoding="utf-8")
        text = re.sub(r'(; 版本: )\d+\.\d+\.\d+', rf'\g<1>{version}', text)
        text = re.sub(r'(#define MyAppVersion ")\d+\.\d+\.\d+(")', rf'\g<1>{version}\g<2>', text)
        iss.write_text(text, encoding="utf-8")
        print(f"  ✓ douzhanzhe-setup.iss")
    
    # package.json
    pkg = root / "package.json"
    if pkg.exists():
        text = pkg.read_text(encoding="utf-8")
        text = re.sub(r'("version":\s*")\d+\.\d+\.\d+(")', rf'\g<1>{version}\g<2>', text)
        pkg.write_text(text, encoding="utf-8")
        print(f"  ✓ package.json")
    
    print(f"  版本号已同步至 {version}")

if __name__ == "__main__":
    if len(sys.argv) < 2:
        print("用法: python sync_version.py <version>")
        sys.exit(1)
    
    version = sys.argv[1]
    # 项目根目录 = 脚本所在目录的上级目录的上级目录 (installer/ 的上级)
    installer_dir = Path(__file__).parent
    root = installer_dir.parent
    
    sync_version(version, root)
