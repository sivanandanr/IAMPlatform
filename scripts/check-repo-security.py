#!/usr/bin/env python3
"""Advisory local hygiene check. Not a replacement for gitleaks/trufflehog/CI scans."""
from pathlib import Path
import re, sys
root=Path(__file__).resolve().parents[1]
patterns=[re.compile(r'-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----'),re.compile(r'(?i)(?:password|client_secret|api_key)\s*[:=]\s*["\'][^"\']{12,}["\']')]
skip={'.git','node_modules','bin','obj','.angular','.venv'}
issues=[]
for p in root.rglob('*'):
    if not p.is_file() or any(x in skip for x in p.parts) or p.stat().st_size>1_000_000:continue
    if p.name=='.env' or p.suffix.lower() in {'.pfx','.p12','.kdbx','.key'}: issues.append(f'Forbidden-looking file: {p.relative_to(root)}');continue
    if p.suffix.lower() not in {'.cs','.ts','.js','.json','.yaml','.yml','.md','.ps1','.sh','.config','.env','.txt'}:continue
    try:s=p.read_text(encoding='utf-8')
    except (UnicodeError,OSError):continue
    for pattern in patterns:
        if pattern.search(s):issues.append(f'Potential secret pattern: {p.relative_to(root)}');break
print('\n'.join(issues) if issues else 'No obvious issues found by basic advisory scan.')
sys.exit(1 if issues else 0)
