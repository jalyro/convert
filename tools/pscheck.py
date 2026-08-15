#!/usr/bin/env python3
"""
Crude PowerShell structural check, plus extraction of PowerShell embedded in
.cmd files.

Not a parser. It catches the classes of breakage that have actually shipped:
  - unbalanced braces/parens/quotes, `elseif`/`else` with no `if` before it
  - PowerShell assembled from caret-continued cmd strings that nothing checked:
    two .NET Core-only APIs (`Process.Kill($true)`,
    `ProcessStartInfo.ArgumentList`) failed silently under Windows
    PowerShell 5.1, and a `#` once commented out the rest of a joined command
  - PS 7-only syntax/APIs in code that runs under the 5.1 host

Exists because there is no PowerShell available on the authoring machine.
CI runs the real `Parser::ParseFile` on .ps1 files, which is the check that
counts; `--extract DIR` writes each embedded block out as a .ps1 so CI can do
the same to those.

Usage:
  pscheck.py [--selftest] [--extract DIR] FILE|GLOB ...

Exit codes: 0 clean, 1 problems found, 2 extractor/selftest failure.
"""
import glob
import pathlib
import re
import sys

# ---------------------------------------------------------------------------
#  Shared string/comment stripping (unchanged behaviour from the .ps1 checker)
# ---------------------------------------------------------------------------

def unterminated_string(text):
    """Offset of a quote that is never closed, or -1.

    The docstring promised balanced quotes and nothing checked them: both
    Write-Host 'x and Write-Host \"x were reported structurally OK.
    """
    i, n = 0, len(text)
    while i < n:
        c = text[i]
        if c == '#' and (i == 0 or text[i-1] != '`'):
            while i < n and text[i] != '\n': i += 1
            continue
        if text.startswith('<#', i):
            j = text.find('#>', i)
            if j < 0: return i
            i = j + 2
            continue
        if c in ('"', "'"):
            start, quote = i, c
            i += 1
            closed = False
            while i < n:
                if text[i] == '`' and quote == '"': i += 2; continue
                if text[i] == quote:
                    if i + 1 < n and text[i+1] == quote: i += 2; continue
                    i += 1; closed = True; break
                i += 1
            if not closed: return start
            continue
        i += 1
    return -1

def strip_strings_and_comments(text):
    out, i, n = [], 0, len(text)
    while i < n:
        c = text[i]
        if c == '#' and (i == 0 or text[i-1] != '`'):
            while i < n and text[i] != '\n': i += 1
            continue
        if text.startswith('<#', i):
            j = text.find('#>', i)
            i = n if j < 0 else j + 2
            continue
        if c in ('"', "'"):
            quote = c; i += 1
            while i < n:
                if text[i] == '`' and quote == '"': i += 2; continue
                if text[i] == quote:
                    if i + 1 < n and text[i+1] == quote: i += 2; continue
                    i += 1; break
                i += 1
            out.append(' ')
            continue
        out.append(c); i += 1
    return ''.join(out)

# ---------------------------------------------------------------------------
#  Structural checks (brace balance, orphaned elseif/else)
# ---------------------------------------------------------------------------

def structural_problems(code, raw=None):
    problems = []
    if raw is not None:
        u = unterminated_string(raw)
        if u >= 0:
            line = raw[:u].count('\n') + 1
            problems.append(f"line {line}: string opened with {raw[u]!r} is never closed")
            return problems  # every later count is meaningless
    for ch, close in (('{','}'), ('(',')'), ('[',']')):
        if code.count(ch) != code.count(close):
            problems.append(f"unbalanced {ch}{close}: {code.count(ch)} open, {code.count(close)} close")
    # elseif / else must follow a closing brace
    for m in re.finditer(r'\b(elseif|else)\b', code):
        before = code[:m.start()].rstrip()
        if not before.endswith('}'):
            line = code[:m.start()].count('\n') + 1
            problems.append(f"line {line}: '{m.group(1)}' does not follow a closing brace")
    return problems

# ---------------------------------------------------------------------------
#  PS 5.1 compatibility checks
#
#  Run on string-stripped code so text in messages cannot false-positive.
#  Applied only to code that runs under the Windows PowerShell 5.1 host:
#  .cmd blocks invoked via `powershell`, and .ps1 files without
#  `#requires -Version 7`. Both API entries are bugs that actually shipped.
# ---------------------------------------------------------------------------

PS51_ONLY = [
    (re.compile(r'\.\s*Kill\s*\(\s*\$true', re.I),
     "Process.Kill($true): the entire-process-tree overload is .NET Core 3.0+;"
     " under 5.1 it throws 'Cannot find an overload' - silent inside try/catch"),
    (re.compile(r'\.\s*ArgumentList\b', re.I),
     "ProcessStartInfo.ArgumentList is .NET Core 2.1+; under 5.1 the property"
     " reads as $null and arguments are silently never passed"),
    (re.compile(r'&&|\|\|'),
     "'&&' / '||' chain operators are PowerShell 7 syntax; parse error under 5.1"),
    (re.compile(r'-Parallel\b', re.I),
     "ForEach-Object -Parallel is PowerShell 7 only"),
]

def ps51_problems(code):
    problems = []
    for rx, why in PS51_ONLY:
        m = rx.search(code)
        if m:
            problems.append(f"5.1-incompatible: '{m.group(0).strip()}' - {why}")
    return problems

def requires_ps7(raw):
    return re.search(r'(?im)^\s*#requires\s+-version\s+7', raw) is not None

# ---------------------------------------------------------------------------
#  '#' inside an embedded block
#
#  At runtime the quoted chunks are joined onto ONE line, so a '#' outside a
#  string comments out everything after it in the whole command. That is the
#  fetch-ffmpeg failure: it reported success and installed nothing.
# ---------------------------------------------------------------------------

def hash_in_block(ps_text):
    i, n = 0, len(ps_text)
    while i < n:
        c = ps_text[i]
        if ps_text.startswith('<#', i):
            j = ps_text.find('#>', i)
            if j < 0:
                return i  # unterminated block comment eats the rest
            i = j + 2
            continue
        if c == '#' and (i == 0 or ps_text[i-1] != '`'):
            return i
        if c in ('"', "'"):
            quote = c; i += 1
            while i < n:
                if ps_text[i] == '`' and quote == '"': i += 2; continue
                if ps_text[i] == quote:
                    if i + 1 < n and ps_text[i+1] == quote: i += 2; continue
                    i += 1; break
                i += 1
            continue
        i += 1
    return -1

# ---------------------------------------------------------------------------
#  .cmd extraction
# ---------------------------------------------------------------------------

def _cmd_scan(line, in_quote):
    """Walk one physical cmd line. Returns (in_quote at EOL, continues).
    cmd rules: '"' toggles, '^' escapes the next char only outside quotes,
    and a trailing unescaped '^' outside quotes continues onto the next line."""
    i, n = 0, len(line)
    while i < n:
        c = line[i]
        if in_quote:
            if c == '"': in_quote = False
            i += 1
        elif c == '"':
            in_quote = True; i += 1
        elif c == '^':
            if i == n - 1:
                return in_quote, True
            i += 2
        else:
            i += 1
    return in_quote, False

def cmd_logical_lines(text):
    """Yields (1-based start line, logical line) with caret continuations
    joined. The trailing caret is dropped; the next line appends as-is."""
    physical = text.replace('\r\n', '\n').split('\n')
    i = 0
    while i < len(physical):
        start = i + 1
        acc, in_quote = '', False
        while True:
            line = physical[i]
            in_quote, cont = _cmd_scan(line, in_quote)
            if cont:
                acc += line[:-1]
                i += 1
                if i >= len(physical): break
                continue
            acc += line
            break
        yield start, acc
        i += 1

def _quote_map(line):
    """True where the char sits inside a cmd double-quoted region.
    Approximation: '\\"' inside PowerShell payloads actually toggles cmd's
    quote state, but the payloads keep cmd specials out of those slivers."""
    flags, in_quote, i, n = [False]*len(line), False, 0, len(line)
    while i < n:
        c = line[i]
        if not in_quote and c == '^' and i + 1 < n:
            flags[i] = flags[i+1] = False; i += 2; continue
        if c == '"': in_quote = not in_quote
        flags[i] = in_quote
        i += 1
    return flags

_HOST_RX = re.compile(r'(?i)\b(powershell|pwsh)(?:\.exe)?\b')

def _param_matches(token, name, min_len=1):
    if not token.startswith(('-', '/')): return False
    t = token[1:].rstrip(':').lower()
    return len(t) >= min_len and name.startswith(t)

def _msvcrt_join(tail):
    """What powershell.exe -Command actually receives: MSVCRT arg parsing
    ('\\"' is a literal quote), args re-joined with single spaces. Unquoted
    cmd specials end the payload; unquoted '^' escapes are undone."""
    args, cur, i, n, in_quote = [], [], 0, len(tail), False
    def flush():
        if cur: args.append(''.join(cur)); cur.clear()
    while i < n:
        c = tail[i]
        if c == '\\':
            j = i
            while j < n and tail[j] == '\\': j += 1
            nb = j - i
            if j < n and tail[j] == '"':
                cur.append('\\' * (nb // 2))
                if nb % 2: cur.append('"')
                else: in_quote = not in_quote
                i = j + 1
            else:
                cur.append('\\' * nb); i = j
            continue
        if c == '"':
            in_quote = not in_quote; i += 1; continue
        if not in_quote:
            if c in '&|<>`': break
            if c == '^':
                i += 1
                if i < n: cur.append(tail[i]); i += 1
                continue
            if c.isspace():
                flush(); i += 1; continue
        cur.append(c); i += 1
    flush()
    return ' '.join(args)

class Block:
    def __init__(self, line, host, text):
        self.line, self.host, self.text = line, host, text

def extract_blocks(text):
    """Returns (blocks, extractor_errors) for one .cmd file."""
    blocks, errors = [], []
    for lineno, logical in cmd_logical_lines(text):
        first = logical.lstrip().lstrip('@')
        tok = first.split(None, 1)[0].lower() if first.split() else ''
        # comments and help text mention invocations without being one
        if tok in ('rem', 'echo', 'echo.') or first.startswith('::'):
            continue
        qmap = _quote_map(logical)
        consumed = 0
        for m in _HOST_RX.finditer(logical):
            if m.start() < consumed or qmap[m.start()]:
                continue
            host = m.group(1).lower()
            # scan unquoted parameter tokens up to -Command; stop if -File
            rest, base = logical[m.end():], m.end()
            payload_at = None; skip = False
            for tm in re.finditer(r'\S+', rest):
                if qmap[base + tm.start()]: continue
                t = tm.group(0)
                if _param_matches(t, 'file'):
                    skip = True; break        # the .ps1 gets checked directly
                if _param_matches(t, 'encodedcommand', min_len=3):
                    errors.append(f"line {lineno}: -EncodedCommand cannot be checked")
                    skip = True; break
                if _param_matches(t, 'command'):
                    payload_at = base + tm.end(); break
            if skip or payload_at is None:
                continue
            ps = _msvcrt_join(logical[payload_at:])
            if not ps.strip():
                errors.append(f"line {lineno}: matched a -Command invocation but extracted nothing")
                continue
            blocks.append(Block(lineno, host, ps))
            consumed = len(logical)  # payload runs to EOL; no second pass inside it
    return blocks, errors

# ---------------------------------------------------------------------------
#  Per-file checks
# ---------------------------------------------------------------------------

def check_ps1(raw):
    problems = structural_problems(strip_strings_and_comments(raw), raw)
    if not requires_ps7(raw):
        problems += ps51_problems(strip_strings_and_comments(raw))
    return problems

def check_block(b):
    problems = []
    h = hash_in_block(b.text)
    if h >= 0:
        problems.append(f"'#' at position {h} of the joined command - at runtime"
                        " it comments out everything after it")
    stripped = strip_strings_and_comments(b.text)
    problems += structural_problems(stripped, b.text)
    if b.host == 'powershell':  # pwsh blocks may use PS 7 APIs
        problems += ps51_problems(stripped)
    return problems

# ---------------------------------------------------------------------------
#  Selftest: proves the extractor extracts, joins, unescapes and flags.
#  Exists because an ad-hoc extractor once returned empty and nobody noticed.
# ---------------------------------------------------------------------------

def selftest():
    fix = (
        '@echo off\r\n'
        'REM powershell -NoProfile -Command "in a comment; must not extract"\r\n'
        'echo powershell -Command "in an echo; must not extract"\r\n'
        'powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0x.ps1" -N 1\r\n'
        'powershell -NoProfile -Command ^\r\n'
        '  "$p = Get-ChildItem \'%ROOT%\\x\';" ^\r\n'
        '  "Write-Host (\'a=\\"\' + $p.Name + \'\\"\')"\r\n'
        'powershell -Command "$psi.ArgumentList.Add(\'x\'); $proc.Kill($true)"\r\n'
        'pwsh -Command "$psi.ArgumentList.Add(\'x\'); $proc.Kill($true)"\r\n'
        'powershell -Command ^\r\n'
        '  "$a = (1,2 # oops" ^\r\n'
        '  "); Write-Host $a"\r\n'
    )
    blocks, errors = extract_blocks(fix)
    checks = [
        ("no extractor errors", not errors),
        ("exactly 4 blocks extracted", len(blocks) == 4),
        ("chunks joined and \\\" unescaped",
         blocks[0].text == '$p = Get-ChildItem \'%ROOT%\\x\'; '
                           'Write-Host (\'a="\' + $p.Name + \'"\')'),
        ("clean block passes", not check_block(blocks[0])),
        ("powershell host flags both 5.1 APIs",
         sum(1 for p in check_block(blocks[1]) if '5.1-incompatible' in p) == 2),
        ("pwsh host is exempt from 5.1 checks", not check_block(blocks[2])),
        ("'#' in a joined command is flagged",
         any("'#'" in p for p in check_block(blocks[3]))),
        ("the '#' also unbalances the parens",
         any('unbalanced' in p for p in check_block(blocks[3]))),
        (".ps1 without #requires 7 flags Kill($true)",
         any('Kill' in p for p in check_ps1('$p.Kill($true)\n'))),
        (".ps1 with #requires 7 is exempt",
         not check_ps1('#requires -Version 7\n$p.Kill($true)\n')),
        ("orphaned else still detected",
         any('else' in p for p in check_ps1('if ($a) { 1 }\nfoo\nelse { 2 }\n'))),
        ("unterminated single quote is caught",
         any('never closed' in p for p in check_ps1("Write-Host 'unfinished\n"))),
        ("unterminated double quote is caught",
         any('never closed' in p for p in check_ps1('Write-Host \"unfinished\n'))),
        ("an apostrophe inside a comment is not a string",
         not check_ps1("# a user's path\nWrite-Host 'ok'\n")),
        ("doubled quotes are an escape, not a terminator",
         not check_ps1("Write-Host 'it''s fine'\n")),
    ]
    ok = True
    for name, passed in checks:
        print(f"  {'pass' if passed else 'FAIL'}: {name}")
        ok = ok and passed
    print(f"selftest: {'OK' if ok else 'FAILED'} ({len(checks)} assertions)")
    return 0 if ok else 2

# ---------------------------------------------------------------------------
#  Main
# ---------------------------------------------------------------------------

def main(argv):
    args = argv[1:]
    if '--selftest' in args:
        return selftest()
    extract_dir = None
    if '--extract' in args:
        i = args.index('--extract')
        if i + 1 >= len(args):
            print("--extract needs a directory"); return 2
        extract_dir = pathlib.Path(args[i+1])
        extract_dir.mkdir(parents=True, exist_ok=True)
        del args[i:i+2]

    # cmd and pwsh callers do not expand globs; do it here
    files = []
    for a in args:
        hits = sorted(glob.glob(a))
        if not hits:
            print(f"{a}: no such file"); return 2
        files += hits

    rc = 0
    for p in files:
        raw = pathlib.Path(p).read_text(encoding='utf-8', errors='replace')
        if p.lower().endswith('.cmd'):
            blocks, errors = extract_blocks(raw)
            if errors:
                rc = 2
                for e in errors: print(f"{p}: EXTRACTOR: {e}")
            if not blocks:
                print(f"{p}: no embedded PowerShell")
                continue
            bad = False
            for k, b in enumerate(blocks, 1):
                if extract_dir is not None:
                    outp = extract_dir / f"{pathlib.Path(p).name}.block{k}.ps1"
                    outp.write_text(
                        f"# extracted from {p} line {b.line} (host: {b.host})\r\n"
                        f"{b.text}\r\n", encoding='utf-8', newline='')
                for prob in check_block(b):
                    bad = True; rc = max(rc, 1)
                    print(f"{p}: block {k} (line {b.line}, {b.host}): {prob}")
            if not bad:
                hosts = ','.join(b.host for b in blocks)
                print(f"{p}: {len(blocks)} embedded block(s) [{hosts}]: OK")
        else:
            issues = check_ps1(raw)
            profile = 'PS 7' if requires_ps7(raw) else 'PS 5.1'
            if issues:
                rc = max(rc, 1)
                print(f"{p}:")
                for i in issues: print(f"  {i}")
            else:
                print(f"{p}: structurally OK ({profile} profile)")
    return rc

if __name__ == '__main__':
    sys.exit(main(sys.argv))
