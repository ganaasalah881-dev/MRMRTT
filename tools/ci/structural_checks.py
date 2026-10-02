"""Lightweight source guards (not a C# compiler). Run from any working directory."""
import json
import re
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

ROOT = Path(__file__).resolve().parents[2]
# Consume literals before comments so URLs / comment-like text in strings are safe.
TOKENS = re.compile(r'@"(?:""|[^"])*"|"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'|//[^\n]*|/\*[\s\S]*?\*/')
EMPTY_HANDLER = re.compile(
    r'\bWith(?:Print|Save|New|Edit|Delete|Search|Refresh|Approve|Unapprove|Excel|Exit|List)'
    r'\(\s*\(\s*_\s*,\s*_\s*\)\s*=>\s*\{\s*\}')
MEMBER = re.compile(r'\bclass\s+(\w+)|\bpublic\s+double\??\s+(\w+)\s*(?:\{|=>)')
# §B110 — الواجهة لا تكتب على القاعدة مباشرة: كل الكتابة عبر طبقة الخدمات
# (الصلاحية + التدقيق وقواعد الأعمال هناك). الاستثناء الوحيد: الإقلاع (ختم الإصدار).
SAVE_CHANGES = re.compile(r'\bSaveChanges\s*\(')
SAVE_CHANGES_ALLOWED = {'Bootstrapper.cs'}


def code_only(text):
    """Blank literals/comments, preserving offsets and line numbers for diagnostics."""
    return TOKENS.sub(lambda m: re.sub(r'[^\n]', ' ', m.group()), text)


def empty_handlers(text):
    return [text.count('\n', 0, m.start()) + 1 for m in EMPTY_HANDLER.finditer(code_only(text))]


def direct_db_writes(path, text):
    """§B110 — مواضع SaveChanges في كود الواجهة (الكتابة المباشرة ممنوعة)."""
    if path.name in SAVE_CHANGES_ALLOWED:
        return []
    return [text.count('\n', 0, m.start()) + 1 for m in SAVE_CHANGES.finditer(code_only(text))]


def double_members(path, text):
    current_class = ''
    members = set()
    for m in MEMBER.finditer(code_only(text)):
        if m[1]:
            current_class = m[1]
        else:
            members.add(f'{path}:{current_class}.{m[2]}')
    return members


def screen_hex_problems(screen_counts, baseline):
    """Reject inline colors on new screens and growth on screens with an approved baseline."""
    problems = []
    for name, count in sorted(screen_counts.items()):
        limit = baseline.get(name)
        if limit is None:
            # A new screen with no inline hex colors is compliant; the previous guard
            # incorrectly rejected it solely because it was absent from the baseline.
            if count > 0:
                problems.append(
                    f'{name}: شاشة جديدة بألوان مضمّنة — استخدم موارد Themes/DateErpTheme.xaml وأضفها للأساس بعد المراجعة.')
        elif count > limit:
            problems.append(f'{name}: الألوان المضمّنة زادت {limit} ← {count} — انقلها إلى موارد الثيم بدل تراكم الانحراف.')
    return problems


def inspect(root):
    problems = []
    for p in (root / 'src').rglob('*.cs'):
        if {'bin', 'obj'} & set(p.parts):
            continue
        text = p.read_text(encoding='utf-8-sig')
        problems.extend(f'{p.relative_to(root)}:{line}: empty toolbar handler'
                        for line in empty_handlers(text))
        if 'DatesErp.Desktop' in p.parts:
            problems.extend(f'{p.relative_to(root)}:{line}: direct SaveChanges in UI — route through a service'
                            for line in direct_db_writes(p, text))

    for p in (root / 'src/DatesErp.Desktop').rglob('*.xaml'):
        if {'bin', 'obj', 'Themes'} & set(p.parts) or p.name == 'App.xaml':
            continue
        element = ET.parse(p).getroot()  # Invalid XML is itself a failing check.
        if element.attrib.get('FlowDirection') != 'RightToLeft':
            problems.append(f'{p.relative_to(root)}: root must declare RightToLeft')

    baseline = json.loads((root / 'tools/ci/legacy-double-members.json').read_text(encoding='utf-8'))
    actual = set()
    for p in (root / 'src/DatesErp.Core/Domain/Entities').glob('*.cs'):
        actual.update(double_members(p.name, p.read_text(encoding='utf-8-sig')))
    for member in sorted(actual - set(baseline['members'])):
        problems.append(f'New double member: {member}. Use decimal or obtain an explicit compatibility review.')
    print(f'Legacy double members: {len(actual)}; no new identities allowed (not just a count ceiling).')

    # §44 — حارس الانحراف البصري: لا يجوز لأي شاشة أن تزيد ألوانها المضمّنة عن الخط الأساسي،
    # والشاشات الجديدة يجب أن تولد بلا hex inline (موارد الثيم فقط).
    hex_base_path = root / 'tools/ci/screen_hex_baseline.json'
    if hex_base_path.exists():
        hex_base = json.loads(hex_base_path.read_text(encoding='utf-8'))
        screen_counts = {
            p.name: len(re.findall(r'#[0-9A-Fa-f]{6}', p.read_text(encoding='utf-8-sig')))
            for p in sorted((root / 'src/DatesErp.Desktop/Views/Screens').glob('*.xaml'))
        }
        problems.extend(screen_hex_problems(screen_counts, hex_base))
        print(f'Inline hex baseline: monitored for {len(hex_base)} screens; growth fails the build.')
    return problems


if __name__ == '__main__':
    findings = inspect(ROOT)
    for finding in findings:
        print(f'ERROR: {finding}', file=sys.stderr)
    print('Structural checks: ' + ('FAILED' if findings else 'PASS'))
    sys.exit(bool(findings))
