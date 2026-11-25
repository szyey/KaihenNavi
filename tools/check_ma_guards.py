#!/usr/bin/env python3
"""MA の任意依存を静的に検査する。Unity のアセンブリ解決は検証しない。"""
import ast
import json
from pathlib import Path
import re
import sys


SYMBOL = "KAIHENNAVI_MA"
PACKAGE = "nadena.dev.modular-avatar"
NAMESPACE = "nadena.dev.modular_avatar"
LITERALS = re.compile(
    r'//[^\n]*|/\*.*?\*/|(?:\$@|@\$|@)"(?:""|[^"])*"'
    r'|\$?"(?:\\.|[^"\\])*"|\'(?:\\.|[^\'\\])*\'', re.S)


def code_only(source):
    def mask(match):
        value = match.group()
        # 補間文字列は式を見落とさないよう、文字列部分も保守的に検査する。
        if value.startswith(('$"', '$@"', '@$"')):
            return value.replace('#', ' ')
        return re.sub(r'[^\n]', ' ', value)
    return LITERALS.sub(mask, source)


def combine(left, right, operation):
    return {operation(a, b) for a in left for b in right}


def negate(values):
    return {not value for value in values}


def without_ma(expression):
    """MA が無い場合の真偽の候補。他のシンボルは両方を検査する。"""
    expression = expression.replace('&&', ' and ').replace('||', ' or ')
    expression = re.sub(r'!(?!=)', ' not ', expression)
    expression = re.sub(r'\btrue\b', 'True', expression)
    expression = re.sub(r'\bfalse\b', 'False', expression)

    def visit(node):
        if isinstance(node, ast.Name):
            return {False} if node.id == SYMBOL else {False, True}
        if isinstance(node, ast.Constant) and type(node.value) is bool:
            return {node.value}
        if isinstance(node, ast.UnaryOp) and isinstance(node.op, ast.Not):
            return negate(visit(node.operand))
        if isinstance(node, ast.BoolOp):
            values = visit(node.values[0])
            for other in node.values[1:]:
                if isinstance(node.op, ast.And):
                    values = combine(values, visit(other), lambda a, b: a and b)
                else:
                    values = combine(values, visit(other), lambda a, b: a or b)
            return values
        if isinstance(node, ast.Compare) and len(node.ops) == 1:
            left, right = visit(node.left), visit(node.comparators[0])
            if isinstance(node.ops[0], ast.Eq):
                return combine(left, right, lambda a, b: a == b)
            if isinstance(node.ops[0], ast.NotEq):
                return combine(left, right, lambda a, b: a != b)
        raise ValueError("unsupported preprocessor expression")

    return visit(ast.parse(expression.strip(), mode='eval').body)


def ma_type_names(stubs):
    sections = re.split(r'(?m)^namespace\s+', code_only(stubs))
    names = set()
    for section in sections[1:]:
        if section.startswith(NAMESPACE + '.'):
            names.update(re.findall(r'\b(?:class|struct|enum|interface)\s+(\w+)', section))
    if not names:
        raise ValueError("MA stub type declarations not found")
    return names


def source_errors(source, names):
    code = code_only(source)
    type_pattern = r'\b(?:' + '|'.join(sorted(names)) + r')\b'
    usage = re.compile(re.escape(NAMESPACE) + r'\b|' + type_pattern)
    # 型・名前空間の別名も、宣言とは別のブロックで使われていないか調べる。
    for alias, target in re.findall(r'\busing\s+(\w+)\s*=\s*([^;]+);', code):
        if usage.search(target):
            names = names | {alias}
    usage = re.compile(usage.pattern + r'|' + r'\b(?:' + '|'.join(sorted(names)) + r')\b')
    active, stack, errors = {True}, [], []
    for number, line in enumerate(code.splitlines(), 1):
        directive = re.match(r'^\s*#\s*(\w+)\b(.*)', line)
        try:
            if directive:
                kind, expression = directive.group(1), directive.group(2).strip()
                if kind == 'if':
                    condition = without_ma(expression)
                    stack.append([active, condition, False])
                    active = combine(active, condition, lambda a, b: a and b)
                elif kind in ('else', 'elif'):
                    if not stack or stack[-1][2]:
                        raise ValueError("unexpected #" + kind)
                    parent, seen, _ = stack[-1]
                    condition = {True} if kind == 'else' else without_ma(expression)
                    active = combine(parent, combine(negate(seen), condition,
                                     lambda a, b: a and b), lambda a, b: a and b)
                    stack[-1] = [parent, combine(seen, condition, lambda a, b: a or b), kind == 'else']
                elif kind == 'endif':
                    if not stack:
                        raise ValueError("unexpected #endif")
                    active = stack.pop()[0]
                elif kind in ('define', 'undef') and expression == SYMBOL:
                    raise ValueError("MA symbol must come from versionDefines")
            elif True in active and usage.search(line):
                errors.append(f"{number}: MA namespace/type is not guarded by #if {SYMBOL}")
        except (SyntaxError, ValueError) as error:
            errors.append(f"{number}: {error}")
    if stack:
        errors.append("unterminated #if")
    return errors


def version_errors(asmdef):
    entries = asmdef.get('versionDefines', [])
    if not any(entry.get('name') == PACKAGE and entry.get('define') == SYMBOL
               and entry.get('expression') == '1.10.0' for entry in entries):
        return [f"versionDefines must define {SYMBOL} for {PACKAGE} >= 1.10.0"]
    return []


def main():
    repo = Path(__file__).resolve().parent.parent
    editor = repo / 'Packages/com.suzuki-y0.jeni.kaihen-navi/Editor'
    asmdef = editor / 'Jeni.KaihenNavi.Editor.asmdef'
    errors = [f"{asmdef.relative_to(repo)}: {error}"
              for error in version_errors(json.loads(asmdef.read_text(encoding='utf-8')))]
    names = ma_type_names((repo / 'tools/stubs/VrcAndMaStubs.cs').read_text(encoding='utf-8'))
    files = sorted(editor.rglob('*.cs'))
    for path in files:
        errors.extend(f"{path.relative_to(repo)}:{error}"
                      for error in source_errors(path.read_text(encoding='utf-8'), names))
    if errors:
        print('\n'.join(errors), file=sys.stderr)
        return 1
    print(f"MA optional-dependency static checks passed ({len(files)} Editor C# files).")
    return 0


if __name__ == '__main__':
    sys.exit(main())
