#!/usr/bin/env python3
"""Live Hyprland overview benchmark; standard library only. See benchmark-overview.md."""
import argparse
import csv
import datetime as dt
import io
import json
import math
import os
from pathlib import Path
import platform
import re
import shlex
import shutil
import signal
import statistics
import subprocess
import sys
import time
import unittest

ROOT = Path(__file__).resolve().parent.parent
DEFAULT_COMMAND = ['dotnet', 'bin/Release/net10.0/HyprNetShell.dll']
PHASES = ('Frame', 'ActiveFrame', 'PollThumbnails', 'DrawOverview', 'Update', 'RefreshState')


def run(command, timeout=10):
    result = subprocess.run(command, cwd=ROOT, text=True, capture_output=True,
                            timeout=timeout, check=False)
    if result.returncode:
        raise RuntimeError(f'{shlex.join(command)} exited {result.returncode}: '
                           f'{result.stderr.strip()}\n{result.stdout.strip()}')
    return result.stdout


def binds():
    return json.loads(run(['hyprctl', '-j', 'binds']))


def registration_command(arguments):
    if len(arguments) != 2 or arguments[0] != 'eval':
        return None
    string = r'"(?:[^"\\]|\\["\\nrt])*"'
    match = re.fullmatch(r'hl\.bind\(("SUPER \+ SUPER_L"), hl\.dsp\.exec_cmd\(('
                         + string + r')\), \{ release = true \}\)', arguments[1])
    return json.loads(match[2]) if match else None


def proxy_main(arguments):
    real, directory, timeout, *forwarded = arguments
    command = registration_command(forwarded)
    if command is None:
        os.execv(real, [real, *forwarded])
    result = subprocess.run([real, *forwarded], timeout=float(timeout), check=False)
    if result.returncode == 0:
        path = Path(directory) / f'{os.getpid()}.json'
        temporary = path.with_suffix('.tmp')
        dump(temporary, {'arguments': forwarded, 'command': command})
        temporary.replace(path)
    return result.returncode


def proxy_environment(result, env, timeout):
    directory = result / 'registrations'
    directory.mkdir()
    proxy_dir = result / 'proxy-bin'
    proxy_dir.mkdir()
    real = shutil.which('hyprctl')
    proxy = proxy_dir / 'hyprctl'
    invocation = shlex.join([sys.executable, str(Path(__file__).resolve()),
                             '--hyprctl-proxy', real, str(directory), str(timeout)])
    proxy.write_text('#!/bin/sh\nexec ' + invocation + ' "$@"\n')
    proxy.chmod(0o700)
    return dict(env, PATH=str(proxy_dir) + os.pathsep + env.get('PATH', '')), directory


def registrations(directory):
    return [json.loads(path.read_text()) for path in sorted(directory.glob('*.json'))]


def owned_command(command, pid):
    # Parse the exact shell command, including ShellQuote's escaped apostrophes.
    try:
        tokens = shlex.split(command)
    except ValueError:
        return False
    return (len(tokens) == 6 and tokens[:1] == ['printf']
            and re.fullmatch(r'[0-9a-f]{32}\\n', tokens[1]) is not None
            and tokens[2:5] == ['|', 'socat', '-']
            and tokens[5].startswith('UNIX-CONNECT:')
            and re.fullmatch(r'.*/hyprnetshell-' + str(pid) + r'-[0-9a-f]{32}\.sock',
                             tokens[5][len('UNIX-CONNECT:'):]) is not None)


def select_bind(current, initial, pid, recorded=()):
    candidates = []
    old = {json.dumps(b, sort_keys=True) for b in initial}
    commands = {r['command'] for r in recorded
                if registration_command(r.get('arguments', [])) == r.get('command')
                and owned_command(r.get('command', ''), pid)}
    for binding in current:
        arg = binding.get('arg', '')
        if (binding.get('key') == 'SUPER_L' and binding.get('modmask') == 64
                and binding.get('release') is True
                and binding.get('submap', '') == ''
                and json.dumps(binding, sort_keys=True) not in old):
            if binding.get('dispatcher') == 'exec' and owned_command(arg, pid):
                candidates.append(binding)
            elif binding.get('dispatcher') == '__lua' and re.fullmatch(r'[0-9]+', arg):
                if len(commands) > 1:
                    raise RuntimeError('Ambiguous PID-owned overview registrations')
                if commands:
                    candidates.append({**binding, 'registered_exec_arg': next(iter(commands)),
                                       'attribution': 'successful child-only hyprctl registration'})
    if len(candidates) > 1:
        raise RuntimeError('Ambiguous overview binding; refusing to dispatch')
    return candidates[0] if candidates else None


def shell_processes():
    found = []
    for directory in Path('/proc').glob('[0-9]*'):
        try:
            args = (directory / 'cmdline').read_bytes().split(b'\0')
            comm = (directory / 'comm').read_text().strip()
            if comm == 'HyprNetShell' or any(
                    Path(os.fsdecode(arg)).name in ('HyprNetShell', 'HyprNetShell.dll')
                    for arg in args if arg):
                found.append(int(directory.name))
        except (OSError, ProcessLookupError):
            continue
    return found


def cpu_ticks(pid):
    # Process stat includes all threads, including managed/native worker threads.
    fields = Path(f'/proc/{pid}/stat').read_text().rsplit(')', 1)[1].split()
    return int(fields[11]) + int(fields[12])


def utc():
    return dt.datetime.now(dt.timezone.utc).isoformat()


def assign_stage(start, end, stages, guard):
    for stage in stages:
        if start >= stage['start_epoch'] + guard and end <= stage['end_epoch'] - guard:
            return stage['name']
    return None


def parse_csv(stream, stages, guard):
    reader = csv.DictReader(stream)
    required = {'timestamp_utc', 'window_seconds', 'phase', 'calls',
                'wall_mean_us', 'wall_p95_us', 'cpu_mean_us', 'cpu_p95_us'}
    if not required.issubset(reader.fieldnames or []):
        raise ValueError('Unrecognized profiling CSV header')
    rows = []
    for raw in reader:
        end = dt.datetime.fromisoformat(raw['timestamp_utc'].replace('Z', '+00:00')).timestamp()
        seconds = float(raw['window_seconds'])
        if seconds <= 0 or not math.isfinite(seconds):
            raise ValueError('Invalid profiling window duration')
        row = dict(raw)
        row['stage'] = assign_stage(end - seconds, end, stages, guard)
        row['window_start_epoch'] = end - seconds
        row['window_end_epoch'] = end
        for key in required - {'timestamp_utc', 'phase'}:
            row[key] = float(raw[key])
            if not math.isfinite(row[key]) or row[key] < 0:
                raise ValueError(f'Invalid CSV value: {key}')
        rows.append(row)
    return rows


def summarize(rows, stages):
    output = {}
    for stage in stages:
        phases = {}
        for phase in PHASES:
            selected = [r for r in rows if r['stage'] == stage['name'] and r['phase'] == phase]
            calls = sum(r['calls'] for r in selected)
            if not calls:
                continue
            values = {'intervals': len(selected), 'calls': calls}
            for metric in ('wall', 'cpu'):
                p95 = [r[f'{metric}_p95_us'] for r in selected]
                values[metric] = {
                    'weighted_mean_us': sum(r[f'{metric}_mean_us'] * r['calls'] for r in selected) / calls,
                    'interval_p95_us': p95,
                    'interval_p95_median_us': statistics.median(p95),
                    'interval_p95_max_us': max(p95),
                }
            phases[phase] = values
        output[stage['name']] = phases
    return output


def dump(path, data):
    path.write_text(json.dumps(data, indent=2) + '\n')


def preflight(command, allow_missing=False):
    if platform.system() != 'Linux':
        raise RuntimeError('Linux is required')
    for tool in ('hyprctl', 'socat', command[0]):
        if not shutil.which(tool):
            raise RuntimeError(f'Executable not found: {tool}')
    if not os.environ.get('HYPRLAND_INSTANCE_SIGNATURE'):
        raise RuntimeError('Run inside the target Hyprland session')
    existing = shell_processes()
    if existing:
        raise RuntimeError(f'Existing HyprNetShell process(es) {existing}; exit your shell manually first')
    if command == DEFAULT_COMMAND and not allow_missing and not (ROOT / command[1]).is_file():
        raise RuntimeError(f'Default DLL missing: {ROOT / command[1]}')
    return binds()


def benchmark(args, command, initial):
    result = (ROOT / args.results / dt.datetime.now().strftime('%Y%m%d-%H%M%S-%f')).resolve()
    result.mkdir(parents=True, exist_ok=False)
    state = result / 'state'
    state.mkdir()
    events, stages = [], []
    child = None
    binding = None
    opened = False
    failure = None

    def event(name, **details):
        events.append({'event': name, 'timestamp_utc': utc(),
                       'monotonic_seconds': time.monotonic(), **details})
        dump(result / 'events.json', events)

    def alive():
        if child.poll() is not None:
            raise RuntimeError(f'Launched process exited with status {child.returncode}')

    def toggle(name):
        nonlocal opened
        alive()
        verified = select_bind(binds(), initial, child.pid, registrations(registration_dir))
        if verified != binding:
            raise RuntimeError('Overview binding changed since discovery; refusing dispatch')
        # Mark conservatively before dispatch: timeout may still deliver the callback.
        opened = not opened
        event(name + '_request')
        command = binding.get('registered_exec_arg', binding['arg'])
        if binding['dispatcher'] == '__lua':
            dispatch = ['hyprctl', 'eval', 'hl.dispatch(hl.dsp.exec_cmd(' + json.dumps(command) + '))']
        else:
            dispatch = ['hyprctl', 'dispatch', 'exec', command]
        run(dispatch, args.command_timeout)
        event(name + '_dispatched')

    def dwell(name, seconds):
        alive()
        start_epoch, start_mono = time.time(), time.monotonic()
        before = cpu_ticks(child.pid)
        event('stage_start', stage=name)
        try:
            deadline = start_mono + seconds
            while time.monotonic() < deadline:
                alive()
                time.sleep(min(0.1, max(0, deadline - time.monotonic())))
        finally:
            end_epoch, end_mono = time.time(), time.monotonic()
            stage = {'name': name, 'start_epoch': start_epoch, 'end_epoch': end_epoch,
                     'duration_seconds': end_mono - start_mono}
            try:
                stage['process_cpu_seconds'] = (cpu_ticks(child.pid) - before) / os.sysconf('SC_CLK_TCK')
                stage['process_cpu_percent'] = 100 * stage['process_cpu_seconds'] / stage['duration_seconds']
            except OSError:
                stage['process_cpu_unavailable'] = True
            stages.append(stage)
            event('stage_end', stage=name)

    metadata = {'command': command, 'created_utc': utc(), 'platform': platform.platform(),
                'machine': platform.machine(), 'cpu_count': os.cpu_count(),
                'python': sys.version, 'initial_binds': initial,
                'parameters': vars(args), 'cpu_scope': 'launched process, all threads; excludes child processes'}
    try:
        for name, cmd in [('monitors', ['hyprctl', '-j', 'monitors']),
                          ('clients', ['hyprctl', '-j', 'clients']),
                          ('hyprland_version', ['hyprctl', '-j', 'version'])]:
            metadata[name] = json.loads(run(cmd, args.command_timeout))
        metadata['cpu_model'] = Path('/proc/cpuinfo').read_text()
        metadata['os_release'] = Path('/etc/os-release').read_text()
        metadata['dotnet_version'] = run(['dotnet', '--version']) if shutil.which('dotnet') else None
        dump(result / 'metadata.json', metadata)
        if args.build:
            event('build_start')
            output = run(['./build.sh', 'build', '-c', 'Release',
                          '-p:HyprNetShellPerformanceProfiling=true'], args.build_timeout)
            (result / 'build.log').write_text(output)
            event('build_end')
        # Repeat refusal check immediately before launch, including after a build.
        preflight(command)
        env = dict(os.environ, HYPRNETSHELL_PROFILE='1',
                   HYPRNETSHELL_PROFILE_INTERVAL_SECONDS='1', XDG_STATE_HOME=str(state))
        env, registration_dir = proxy_environment(result, env, args.command_timeout)
        with (result / 'shell.log').open('w') as log:
            try:
                event('launch_request')
                child = subprocess.Popen(command, cwd=ROOT, env=env, stdout=log, stderr=log)
                event('launched', pid=child.pid)
                deadline = time.monotonic() + args.binding_timeout
                while time.monotonic() < deadline:
                    alive()
                    current = binds()
                    dump(result / 'last-binds.json', current)
                    binding = select_bind(current, initial, child.pid, registrations(registration_dir))
                    if binding:
                        dump(result / 'selected-bind.json', binding)
                        event('binding_ready')
                        break
                    time.sleep(0.2)
                if not binding:
                    raise RuntimeError('Timed out waiting for PID-owned overview binding')
                dwell('startup_warmup', args.warmup)
                dwell('idle', args.idle)
                for index in range(args.cycles):
                    toggle(f'open_{index + 1}')
                    dwell(f'open_{index + 1}', args.dwell)
                    toggle(f'close_{index + 1}')
                    dwell(f'closed_{index + 1}', args.dwell)
                dwell('cooloff', args.cooloff)
            finally:
                if child is not None and child.poll() is None:
                    if opened and binding:
                        try:
                            toggle('cleanup_close')
                            time.sleep(0.5)
                        except Exception as exc:
                            event('cleanup_close_failed', error=str(exc))
                    event('stop_request')
                    child.send_signal(signal.SIGINT)
                    try:
                        child.wait(timeout=5)
                    except subprocess.TimeoutExpired:
                        child.terminate()
                        try:
                            child.wait(timeout=3)
                        except subprocess.TimeoutExpired:
                            child.kill()
                            child.wait(timeout=3)
                    event('stopped', returncode=child.returncode)
    except (Exception, KeyboardInterrupt) as exc:
        failure = f'{type(exc).__name__}: {exc}'
        event('failure', error=failure)
    dump(result / 'metadata.json', metadata)
    rows = []
    try:
        files = sorted(state.rglob('performance-*.csv'))
        if not files:
            raise RuntimeError('No profiling CSV: build with HyprNetShellPerformanceProfiling=true')
        for path in files:
            with path.open(newline='') as stream:
                rows.extend(parse_csv(stream, stages, args.transition_guard))
        if not rows:
            raise RuntimeError('Profiling CSV has no samples')
    except Exception as exc:
        failure = '; '.join(filter(None, [failure, f'CSV: {exc}']))
    summary = summarize(rows, stages)
    warnings = [f'{phase} absent from CSV' for phase in PHASES if not any(r['phase'] == phase for r in rows)]
    warnings += [f'{stage["name"]}: no fully contained profiling intervals' for stage in stages
                 if not summary[stage['name']]]
    report = {'failure': failure, 'warnings': warnings, 'stages': stages, 'summary': summary,
              'excluded_rows': sum(r['stage'] is None for r in rows)}
    dump(result / 'report.json', report)
    dump(result / 'stages.json', stages)
    if rows:
        with (result / 'intervals.csv').open('w', newline='') as stream:
            writer = csv.DictWriter(stream, fieldnames=list(rows[0]))
            writer.writeheader()
            writer.writerows(rows)
    lines = ['# Overview benchmark', '', f'Command: `{shlex.join(command)}`', '',
             f'Status: {failure or "completed"}', '',
             'Callback dispatch, not physical key injection. Durations include transitions; CSV summaries exclude them.',
             'CPU is percent of one core, all launched-process threads; not child processes.',
             'Means are call-weighted. p95 values are interval distributions, NOT pooled percentiles.', '',
             '| Stage | Phase | Calls | Wall mean µs | Interval wall p95 median / max µs | CPU mean µs |',
             '|---|---|---:|---:|---:|---:|']
    for stage, phases in summary.items():
        for phase, values in phases.items():
            wall, cpu = values['wall'], values['cpu']
            lines.append(f'| {stage} | {phase} | {values["calls"]:g} | {wall["weighted_mean_us"]:.3f} | '
                         f'{wall["interval_p95_median_us"]:.3f} / {wall["interval_p95_max_us"]:.3f} | '
                         f'{cpu["weighted_mean_us"]:.3f} |')
    lines += ['', '## Process CPU by stage', '']
    for stage in stages:
        lines.append(f'- {stage["name"]}: {stage.get("process_cpu_percent", "unavailable")} %')
    lines += ['', '## Warnings', ''] + [f'- {warning}' for warning in warnings]
    (result / 'report.md').write_text('\n'.join(lines) + '\n')
    print(f'Results: {result}')
    if failure:
        print(failure, file=sys.stderr)
    return 1 if failure else 0


class SelfTests(unittest.TestCase):
    def test_binding(self):
        binding = {'key': 'SUPER_L', 'modmask': 64, 'release': True, 'dispatcher': 'exec',
                   'arg': "printf '" + 'a' * 32 + "\\n' | socat - UNIX-CONNECT:'/tmp/hyprnetshell-42-" + 'b' * 32 + ".sock'"}
        self.assertEqual(select_bind([binding], [], 42), binding)
        self.assertIsNone(select_bind([binding], [binding], 42))
        self.assertIsNone(select_bind([binding], [], 43))
        for key, value in [('release', False), ('modmask', 0), ('dispatcher', 'kill'), ('key', 'A')]:
            self.assertIsNone(select_bind([{**binding, key: value}], [], 42))
        with self.assertRaises(RuntimeError):
            select_bind([binding, {**binding, 'description': 'duplicate'}], [], 42)

    def test_lua_registration(self):
        command = "printf '" + 'a' * 32 + "\\n' | socat - UNIX-CONNECT:'/tmp/hyprnetshell-42-" + 'b' * 32 + ".sock'"
        arguments = ['eval', 'hl.bind("SUPER + SUPER_L", hl.dsp.exec_cmd('
                     + json.dumps(command) + '), { release = true })']
        recorded = [{'arguments': arguments, 'command': command}]
        lua = {'key': 'SUPER_L', 'modmask': 64, 'release': True,
               'dispatcher': '__lua', 'arg': '556', 'submap': ''}
        self.assertEqual(registration_command(arguments), command)
        self.assertIsNone(select_bind([lua], [], 42))
        selected = select_bind([lua], [], 42, recorded)
        self.assertEqual(selected['registered_exec_arg'], command)
        self.assertIsNone(select_bind([lua], [lua], 42, recorded))
        self.assertIsNone(select_bind([lua], [], 43, recorded))
        self.assertIsNone(select_bind([{**lua, 'submap': 'other'}], [], 42, recorded))
        self.assertIsNone(select_bind([lua], [], 42, [{'arguments': arguments, 'command': command + '; evil'}]))
        self.assertIsNone(registration_command(['eval', arguments[1].replace('release = true', 'release = false')]))
        self.assertIsNone(registration_command(['eval', arguments[1].replace('SUPER + SUPER_L', 'Print')]))
        self.assertFalse(owned_command(command + '; evil', 42))
        self.assertTrue(owned_command(command.replace('/tmp/', '/tmp/a b/'), 42))
        with self.assertRaises(RuntimeError):
            select_bind([lua, {**lua, 'arg': '557'}], [], 42, recorded)
        other = command.replace('a' * 32, 'c' * 32)
        with self.assertRaises(RuntimeError):
            select_bind([lua], [], 42, recorded + [{'command': other,
                        'arguments': ['eval', arguments[1].replace('a' * 32, 'c' * 32)]}])

    def test_csv_and_stages(self):
        stages = [{'name': 'idle', 'start_epoch': 0, 'end_epoch': 5},
                  {'name': 'open', 'start_epoch': 5, 'end_epoch': 10}]
        self.assertIsNone(assign_stage(4.5, 5.5, stages, 0))
        self.assertIsNone(assign_stage(0, 1, stages, 0.1))
        text = ('timestamp_utc,window_seconds,phase,calls,wall_mean_us,wall_p95_us,cpu_mean_us,cpu_p95_us\n'
                '1970-01-01T00:00:02Z,1,ActiveFrame,2,10,20,5,9\n'
                '1970-01-01T00:00:03Z,1,ActiveFrame,6,30,40,15,19\n'
                '1970-01-01T00:00:05.5Z,1,Frame,1,99,99,99,99\n')
        rows = parse_csv(io.StringIO(text), stages, 0.1)
        self.assertEqual([r['stage'] for r in rows], ['idle', 'idle', None])
        summary = summarize(rows, stages)['idle']['ActiveFrame']
        self.assertEqual(summary['wall']['weighted_mean_us'], 25)
        self.assertEqual(summary['wall']['interval_p95_us'], [20, 40])
        with self.assertRaises(ValueError):
            parse_csv(io.StringIO('wrong,header\n'), stages, 0)


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--self-test', action='store_true')
    parser.add_argument('--dry-run', '--preflight', action='store_true', dest='dry_run')
    parser.add_argument('--build', action='store_true')
    parser.add_argument('--results', default='profiling/overview')
    parser.add_argument('--cycles', type=int, default=5)
    for name, default in [('warmup', 10), ('idle', 6), ('dwell', 5), ('cooloff', 6),
                          ('transition-guard', 0.25), ('binding-timeout', 30),
                          ('command-timeout', 10), ('build-timeout', 600)]:
        parser.add_argument('--' + name, type=float, default=default)
    parser.add_argument('command', nargs=argparse.REMAINDER, help='-- executable [arguments...]')
    args = parser.parse_args()
    if args.self_test:
        return 0 if unittest.TextTestRunner(verbosity=2).run(
            unittest.defaultTestLoader.loadTestsFromTestCase(SelfTests)).wasSuccessful() else 1
    command = args.command
    if command[:1] == ['--']:
        command = command[1:]
    command = command or DEFAULT_COMMAND.copy()
    if args.cycles < 1 or any(not math.isfinite(getattr(args, name)) or getattr(args, name) < 3
                             for name in ('warmup', 'idle', 'dwell', 'cooloff')):
        parser.error('cycles must be >=1 and all stage durations must be finite and >=3 seconds')
    for name in ('binding_timeout', 'command_timeout', 'build_timeout'):
        if not math.isfinite(getattr(args, name)) or getattr(args, name) <= 0:
            parser.error(f'{name} must be finite and positive')
    if not math.isfinite(args.transition_guard) or args.transition_guard < 0:
        parser.error('transition guard must be finite and nonnegative')
    try:
        initial = preflight(command, allow_missing=args.build)
        if args.dry_run:
            print(json.dumps({'command': command, 'default_dll_exists': (ROOT / DEFAULT_COMMAND[1]).is_file(),
                              'initial_bind_count': len(initial), 'build_requested': args.build,
                              'note': 'Preflight only: no build, launch, dispatch or result directory'}, indent=2))
            return 0
        return benchmark(args, command, initial)
    except (Exception, KeyboardInterrupt) as exc:
        print(f'{type(exc).__name__}: {exc}', file=sys.stderr)
        return 1


if __name__ == '__main__':
    if sys.argv[1:2] == ['--hyprctl-proxy']:
        sys.exit(proxy_main(sys.argv[2:]))
    sys.exit(main())
