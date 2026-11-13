"""Compile the TSV catalogue into plain C# data; HybridCLR need not read DLL resources."""
from pathlib import Path
import json

ROOT = Path(__file__).resolve().parents[1]


def generate():
    lines = ['// Generated from RelicCatalog.tsv by tools/generate_catalog_code.py.',
             '// Keep all runtime catalogue data in managed code for HybridCLR compatibility.',
             'namespace PinnedRelicsMod', '{', '    internal static class RelicCatalogData',
             '    {', '        internal static RelicIcon[] Create()', '        {',
             '            return new RelicIcon[]', '            {']
    for row in (ROOT / 'RelicCatalog.tsv').read_text(encoding='utf-8').splitlines():
        relic_id, name = row.split('\t')
        args = ', '.join([str(int(relic_id)), json.dumps(name, ensure_ascii=False)])
        lines.append('                new RelicIcon(' + args + '),')
    lines.extend(['            };', '        }', '    }', '}', ''])
    (ROOT / 'RelicCatalogData.cs').write_text('\n'.join(lines), encoding='utf-8')


if __name__ == '__main__':
    generate()
