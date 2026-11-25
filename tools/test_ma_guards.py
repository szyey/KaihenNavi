#!/usr/bin/env python3
"""MA 静的検査の正常・空・違反・入れ子分岐を確認する。"""
from pathlib import Path

from check_ma_guards import ma_type_names, source_errors, version_errors


repo = Path(__file__).resolve().parent.parent
names = ma_type_names((repo / 'tools/stubs/VrcAndMaStubs.cs').read_text(encoding='utf-8'))
checks = 0


def check(source, valid):
    global checks
    errors = source_errors(source, names)
    assert (not errors) == valid, (source, errors)
    checks += 1


check('', True)
check('class Plain {}', True)
check('using nadena.dev.modular_avatar.core;', False)
check('#if KAIHENNAVI_MA\nusing nadena.dev.modular_avatar.core;\n#endif', True)
check('nadena.dev.modular_avatar.core.NewType value;', False)
check('#if KAIHENNAVI_MA\nnadena.dev.modular_avatar.core.NewType value;\n#endif', True)
for name in sorted(names):
    check(f'{name} value;', False)
    check(f'#if KAIHENNAVI_MA\n{name} value;\n#endif', True)
check('#if KAIHENNAVI_MA\n#else\nChangedShape value;\n#endif', False)
check('#if !KAIHENNAVI_MA\n#else\nChangedShape value;\n#endif', True)
check('#if OTHER\n#elif KAIHENNAVI_MA\nChangedShape value;\n#endif', True)
check('#if KAIHENNAVI_MA\n#elif OTHER\nChangedShape value;\n#endif', False)
check('#if KAIHENNAVI_MA && OTHER\nChangedShape value;\n#endif', True)
check('#if KAIHENNAVI_MA || OTHER\nChangedShape value;\n#endif', False)
check('#if (KAIHENNAVI_MA == true)\nChangedShape value;\n#endif', True)
check('#if KAIHENNAVI_MA != false\nChangedShape value;\n#endif', True)
check('#if OTHER\nChangedShape value;\n#endif', False)
check('#if KAIHENNAVI_MA\n#if OTHER\n#else\nChangedShape value;\n#endif\n#endif', True)
check('#if OTHER\n#if KAIHENNAVI_MA\n#endif\nChangedShape value;\n#endif', False)
check('#if KAIHENNAVI_MA\nChangedShape value;\n#endif\nChangedShape other;', False)
check('#if KAIHENNAVI_MA\nusing MA = nadena.dev.modular_avatar.core;\n#endif\nMA.NewType value;', False)
check('#if KAIHENNAVI_MA\nusing Shape = nadena.dev.modular_avatar.core.ChangedShape;\nShape value;\n#endif', True)
check('// ChangedShape\n/* nadena.dev.modular_avatar.core\nModularAvatarMenuItem */', True)
check('const string ReflectionName = "nadena.dev.modular_avatar.core.ModularAvatarMeshCutter";', True)
check('const string Text = @"ChangedShape\n""nadena.dev.modular_avatar.core""";', True)
check('const string ModularAvatarUrl = "https://modular-avatar.nadena.dev/";', True)
check('var text = $"{typeof(ChangedShape)}";', False)
check('#if KAIHENNAVI_MA\nvar text = $"{typeof(ChangedShape)}";\n#endif', True)
check('#if false\nChangedShape value;\n#endif', True)
check('#if KAIHENNAVI_MA\n', False)
check('#endif', False)
check('#else', False)
check('#if OTHER\n#else\n#else\n#endif', False)
check('#if OTHER + 1\n#endif', False)
check('#define KAIHENNAVI_MA', False)
check('#undef KAIHENNAVI_MA', False)

entry = {'name': 'nadena.dev.modular-avatar', 'expression': '1.10.0', 'define': 'KAIHENNAVI_MA'}
assert not version_errors({'versionDefines': [entry]})
checks += 1
for asmdef in ({}, {'versionDefines': []},
               *({'versionDefines': [dict(entry, **{key: 'wrong'})]} for key in entry)):
    assert version_errors(asmdef), asmdef
    checks += 1
try:
    ma_type_names('namespace Other { class NotMA {} }')
    raise AssertionError('missing MA stubs were accepted')
except ValueError:
    checks += 1

print(f'MA guard regression tests passed ({checks} checks).')
