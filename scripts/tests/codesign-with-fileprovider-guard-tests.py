#!/usr/bin/python3
"""Stdlib tests: FileProvider is always fake, including detached-process tests."""

import contextlib
import errno
import functools
import importlib.util
import io
import json
import os
from pathlib import Path
import select
import shutil
import signal
import socket
import subprocess
import sys
import time
import unittest
from unittest import mock
import uuid


sys.dont_write_bytecode = True
SCRIPT = Path(__file__).resolve().parents[1] / "codesign-with-fileprovider-guard.py"
spec = importlib.util.spec_from_file_location("fileprovider_guard", SCRIPT)
guard = importlib.util.module_from_spec(spec)
spec.loader.exec_module(guard)


class FakeProcesses:
    """A file-backed fake visible across forks; never signals an OS process."""

    def __init__(self, events):
        self.events = events
        self.provider = guard.Process(
            424242, os.getuid(), (100, 123456), os.path.realpath(guard.DAEMON), False)
        self.caller = guard.Process(424243, os.getuid(), (99, 111111), "/fake/build", False)
        self.caller_dead = events.with_suffix(".caller-dead")
        self.fail_stop = False
        self.fail_resume = False
        self.changed_after_stop = False

    def history(self):
        return [json.loads(line) for line in self.events.read_text().splitlines()]

    def service_pid(self):
        return self.provider.pid

    def process(self, pid):
        if pid == self.caller.pid:
            return None if self.caller_dead.exists() else self.caller
        if pid != self.provider.pid:
            return None
        events = self.history()
        provider = self.provider
        if events:
            provider = provider._replace(stopped=events[-1] == "STOP")
            if self.changed_after_stop:
                provider = provider._replace(start=(101, 123456))
        return provider

    def send_signal(self, pid, signum):
        assert pid == self.provider.pid
        assert signum in (signal.SIGSTOP, signal.SIGCONT)
        if (signum == signal.SIGSTOP and self.fail_stop
                or signum == signal.SIGCONT and self.fail_resume):
            raise PermissionError(errno.EPERM, "Operation not permitted")
        if signum == signal.SIGSTOP:
            self.events.with_suffix(".guardian").write_text(json.dumps({
                "pid": os.getpid(), "parent": os.getppid(),
                "group": os.getpgrp(), "session": os.getsid(0)}))
        with self.events.open("a") as stream:
            stream.write(json.dumps("STOP" if signum == signal.SIGSTOP else "CONT") + "\n")


class GuardTests(unittest.TestCase):
    def setUp(self):
        # Scratch stays in this checkout; no system temporary directories.
        self.root = Path("scripts/tests") / (".fileprovider-guard-tests-" + uuid.uuid4().hex)
        self.root.mkdir(mode=0o700)
        self.home = self.root.resolve() / "home"
        self.home.mkdir()
        self.events = self.root.resolve() / "signals"
        self.events.write_text("")
        self.processes = FakeProcesses(self.events)
        self.lock_factory = functools.partial(guard.open_lock, self.home)
        self.channels = []
        self.children = set()
        self.real_signals = mock.patch.object(
            guard.DarwinProcesses, "send_signal",
            side_effect=AssertionError("Tests must never signal the real FileProvider."))
        self.real_signals.start()

    def tearDown(self):
        try:
            for pid in self.children:
                try:
                    os.kill(pid, signal.SIGKILL)
                except ProcessLookupError:
                    pass
                os.waitpid(pid, 0)
            for channel in self.channels:
                if channel.connection.fileno() >= 0:
                    try:
                        if channel.final is None:
                            channel.connection.sendall(b"D")
                            while channel.receive(3)[0] == "READY":
                                pass
                    except (OSError, guard.GuardError):
                        pass
                    channel.connection.close()
        finally:
            self.real_signals.stop()
            shutil.rmtree(self.root)

    def launch(self, timeout=3, caller=None):
        channel = guard.start_guardian(
            timeout, self.processes, self.lock_factory, caller=caller)
        self.channels.append(channel)
        return channel

    def release(self, channel):
        channel.connection.sendall(b"D")
        result = channel.receive(3)
        channel.connection.close()
        return result

    def wait_for(self, predicate):
        deadline = time.monotonic() + 5
        while not predicate():
            if time.monotonic() > deadline:
                self.fail("Timed out waiting for a fake-process event.")
            time.sleep(0.01)

    def fork_worker(self, operation):
        pid = os.fork()
        if pid == 0:
            try:
                status = operation()
            except BaseException:
                os._exit(99)
            os._exit(status or 0)
        self.children.add(pid)
        return pid

    def wait_worker(self, pid):
        result = []

        def finished():
            found, status = os.waitpid(pid, os.WNOHANG)
            if found:
                result.append(status)
            return bool(found)

        self.wait_for(finished)
        self.children.remove(pid)
        return os.waitstatus_to_exitcode(result[0])

    def sign(self, arguments=None, timeout=3):
        return guard.guarded_sign(
            arguments or ["--sign", "-", "/fake/Target.app"], Path("/fake/Target.app"),
            timeout, self.processes, self.lock_factory)

    def test_managed_target_normalizes_relative_paths_and_symlinks(self):
        cloud = self.home / "Library" / "CloudStorage"
        target = cloud / "OneDrive" / "App.app"
        target.mkdir(parents=True)
        link = self.root / "link"
        link.symlink_to(target, target_is_directory=True)
        for path in (str(target), str(link), os.path.relpath(target)):
            with self.subTest(path=path):
                self.assertEqual(guard.managed_target(["--sign", "-", path], self.home), target)
        outside = self.home / "App.app"
        outside.mkdir()
        (cloud / "escape").symlink_to(outside, target_is_directory=True)
        self.assertIsNone(guard.managed_target(["--sign", "-", str(cloud / "escape")], self.home))

    def test_unmanaged_and_other_user_paths_bypass(self):
        for target in (self.home / "build" / "App.app",
                       self.home / "Library" / "CloudStorageOther" / "App.app",
                       self.home.parent / "another-user" / "Library" / "CloudStorage" / "App.app"):
            self.assertIsNone(guard.managed_target(["--sign", "-", str(target)], self.home))

    def test_non_signing_calls_bypass(self):
        target = str(self.home / "Library" / "CloudStorage" / "App.app")
        for arguments in ([], ["--version"], ["--verify", "--deep", target],
                          ["-d", "--entitlements", ":-", target], ["-v", target]):
            self.assertIsNone(guard.managed_target(arguments, self.home))

    def test_native_sign_option_spellings(self):
        target = self.home / "Library" / "CloudStorage" / "App.app"
        for options in (["--sign", "-"], ["--sign=-"], ["-s", "-"], ["-s-"]):
            self.assertEqual(guard.managed_target(options + [str(target)], self.home), target)

    def test_real_home_ignores_environment_home(self):
        with mock.patch.dict(os.environ, {"HOME": "/a/different/home"}), \
                mock.patch.object(guard.pwd, "getpwuid") as lookup:
            lookup.return_value.pw_dir = str(self.home)
            self.assertEqual(guard.user_home(), self.home)
            lookup.assert_called_once_with(os.getuid())

    def test_bypass_exec_preserves_arguments_without_starting_guardian(self):
        arguments = ["--verify", "--verbose=4", "/path with spaces/$literal;name.app"]
        class Executed(Exception):
            pass
        with mock.patch.object(guard, "start_guardian") as start, \
                mock.patch.object(guard.os, "execv", side_effect=Executed) as execute:
            with self.assertRaises(Executed):
                guard.main(arguments)
            execute.assert_called_once_with(guard.CODESIGN, [guard.CODESIGN] + arguments)
            start.assert_not_called()

    def test_bypass_preserves_native_exit_status(self):
        native_exec = os.execv

        def worker():
            with mock.patch.object(guard.os, "execv",
                                   side_effect=lambda *_: native_exec(
                                       sys.executable, [sys.executable, "-c", "raise SystemExit(37)"])):
                return guard.main(["--verify", "/outside/App.app"])

        self.assertEqual(self.wait_worker(self.fork_worker(worker)), 37)

    def test_timeout_configuration_is_bounded(self):
        self.assertEqual(guard.timeout_seconds({}), 60)
        for value in ("1", "300", "2.5"):
            self.assertEqual(guard.timeout_seconds(
                {"FILEPROVIDER_CODESIGN_TIMEOUT_SECONDS": value}), float(value))
        for value in ("0", "-1", "301", "nan", "inf", "", "not a number"):
            with self.assertRaises(guard.GuardError):
                guard.timeout_seconds({"FILEPROVIDER_CODESIGN_TIMEOUT_SECONDS": value})

    def test_lock_is_private_stable_and_not_unlinked(self):
        with self.lock_factory() as lock:
            info = os.fstat(lock.fileno())
            self.assertEqual(info.st_mode & 0o777, 0o600)
            name = self.home / "Library/Caches/SeattleCarsInBikeLanes.CodesignGuard/fileprovider.lock"
            self.assertEqual(name.parent.stat().st_mode & 0o777, 0o700)
        with self.lock_factory() as lock:
            self.assertEqual(os.fstat(lock.fileno()).st_ino, info.st_ino)
        self.assertTrue(name.exists())

    def test_lock_rejects_symlink_and_nonprivate_directory(self):
        with self.lock_factory():
            pass
        directory = self.home / "Library/Caches/SeattleCarsInBikeLanes.CodesignGuard"
        directory.chmod(0o755)
        with self.assertRaises(guard.GuardError):
            self.lock_factory()
        directory.chmod(0o700)
        lock = directory / "fileprovider.lock"
        lock.unlink()
        lock.symlink_to(self.events)
        with self.assertRaises(OSError):
            self.lock_factory()

    def test_lock_rejects_cloudstorage_cache(self):
        cloud = self.home / "Library" / "CloudStorage"
        cloud.mkdir(parents=True)
        (self.home / "Library" / "Caches").symlink_to(cloud, target_is_directory=True)
        with self.assertRaisesRegex(guard.GuardError, "outside CloudStorage"):
            self.lock_factory()

    def test_service_lookup_requires_exact_label_and_pid(self):
        # Construct without loading Darwin's library, so this also runs on Linux CI.
        processes = object.__new__(guard.DarwinProcesses)
        good = '{\n "Label" = "com.apple.FileProvider";\n "PID" = 123;\n};'
        with mock.patch.object(guard.subprocess, "run") as run:
            run.return_value = subprocess.CompletedProcess([], 0, good, "")
            self.assertEqual(processes.service_pid(), 123)
            self.assertEqual(run.call_args.args[0], ["/bin/launchctl", "list", guard.SERVICE])
            for output, status in ((good.replace("FileProvider", "FileProviderOther"), 0),
                                   (good.replace('"PID" = 123;', ""), 0),
                                   (good.replace("123", "1"), 0), (good, 1),
                                   (good + '\n"PID" = 456;', 0)):
                run.return_value = subprocess.CompletedProcess([], status, output, "")
                with self.assertRaises(guard.GuardError):
                    processes.service_pid()

    def test_identity_requires_exact_executable_current_uid_and_running_state(self):
        good = self.processes.provider
        for provider in (None, good._replace(uid=os.getuid() + 1),
                         good._replace(executable=guard.DAEMON + "-other"),
                         good._replace(pid=1), good._replace(stopped=True)):
            with mock.patch.object(self.processes, "process", return_value=provider), \
                    mock.patch.object(self.processes, "send_signal") as send:
                with self.assertRaises(guard.GuardError):
                    guard.identify_provider(self.processes)
                send.assert_not_called()

    def test_only_trusted_daemon_path_is_normalized(self):
        executable = self.root.resolve() / "canonical-fileproviderd"
        executable.touch()
        trusted_alias = self.root.resolve() / "trusted-framework-alias"
        trusted_alias.symlink_to(executable)
        untrusted_alias = self.root.resolve() / "untrusted-alias"
        untrusted_alias.symlink_to(executable)
        self.processes.provider = self.processes.provider._replace(executable=str(executable))
        with mock.patch.object(guard, "DAEMON", str(trusted_alias)):
            self.assertEqual(guard.identify_provider(self.processes), self.processes.provider)
            self.processes.provider = self.processes.provider._replace(executable=str(untrusted_alias))
            with self.assertRaisesRegex(guard.GuardError, "executable or user ownership"):
                guard.identify_provider(self.processes)

    def test_identity_change_prevents_continue(self):
        for changed in (None, self.processes.provider._replace(start=(100, 123457)),
                        self.processes.provider._replace(pid=424244),
                        self.processes.provider._replace(uid=os.getuid() + 1),
                        self.processes.provider._replace(executable="/fake/other")):
            with mock.patch.object(self.processes, "process", return_value=changed), \
                    mock.patch.object(self.processes, "send_signal") as send, \
                    mock.patch.object(guard.time, "sleep"):
                with self.assertRaisesRegex(guard.GuardError, "resume could not be verified"):
                    guard.resume_provider(self.processes, self.processes.provider)
                send.assert_not_called()

    def test_already_stopped_daemon_is_not_resumed(self):
        self.processes.provider = self.processes.provider._replace(stopped=True)
        channel = self.launch()
        kind, message = channel.receive(3)
        self.assertEqual(kind, "ERROR")
        self.assertIn("already suspended", message)
        self.assertEqual(self.processes.history(), [])

    def test_stop_permissions_fail_closed(self):
        self.processes.fail_stop = True
        channel = self.launch()
        kind, message = channel.receive(3)
        self.assertEqual(kind, "ERROR")
        self.assertIn("not permitted", message)
        self.assertEqual(self.processes.history(), [])

    def test_suspension_between_discovery_and_stop_is_not_taken_over(self):
        provider = self.processes.provider
        with mock.patch.object(self.processes, "process",
                               side_effect=[provider, provider._replace(stopped=True)]):
            channel = self.launch()
        kind, message = channel.receive(3)
        self.assertEqual(kind, "ERROR")
        self.assertIn("became suspended", message)
        self.assertEqual(self.processes.history(), [])

    def test_pending_stop_is_cancelled_even_without_observed_stopped_state(self):
        with mock.patch.object(self.processes, "process", return_value=self.processes.provider):
            channel = self.launch(timeout=0.15)
        kind, message = channel.receive(3)
        self.assertEqual(kind, "ERROR")
        self.assertIn("stopped/resumed state", message)
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])

    def test_detached_guardian_stops_and_resumes_before_acknowledgement(self):
        channel = self.launch()
        self.assertEqual(channel.receive(3)[0], "READY")
        self.assertEqual(self.processes.history(), ["STOP"])
        metadata = json.loads(self.events.with_suffix(".guardian").read_text())
        self.assertNotEqual(metadata["group"], os.getpgrp())
        self.assertNotEqual(metadata["parent"], metadata["session"])
        self.assertNotEqual(metadata["pid"], metadata["session"])
        self.assertEqual(self.release(channel)[0], "RESUMED")
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])

    def test_overlapping_signers_are_serialized(self):
        first = self.launch()
        self.assertEqual(first.receive(3)[0], "READY")
        second = self.launch()
        self.assertFalse(second.readable(0.15))
        self.assertEqual(self.processes.history(), ["STOP"])
        self.assertEqual(self.release(first)[0], "RESUMED")
        self.assertEqual(second.receive(3)[0], "READY")
        self.assertEqual(self.release(second)[0], "RESUMED")
        self.assertEqual(self.processes.history(), ["STOP", "CONT", "STOP", "CONT"])

    def test_waiting_signer_timeout_does_not_resume_active_signer(self):
        first = self.launch()
        self.assertEqual(first.receive(3)[0], "READY")
        second = self.launch(timeout=0.15)
        kind, message = second.receive(3)
        self.assertEqual(kind, "ERROR")
        self.assertIn("another FileProvider signer", message)
        self.assertEqual(self.processes.history(), ["STOP"])
        self.assertEqual(self.release(first)[0], "RESUMED")

    def test_abandoned_waiter_does_not_pause_or_resume_provider(self):
        first = self.launch()
        self.assertEqual(first.receive(3)[0], "READY")
        second = self.launch()
        self.assertFalse(second.readable(0.1))
        second.connection.close()
        self.assertEqual(self.release(first)[0], "RESUMED")
        # A third signer verifies the abandoned waiter released its descriptors.
        third = self.launch()
        self.assertEqual(third.receive(3)[0], "READY")
        self.assertEqual(self.release(third)[0], "RESUMED")
        self.assertEqual(self.processes.history(), ["STOP", "CONT", "STOP", "CONT"])

    def test_watcher_timeout_resumes_and_reports_failure(self):
        channel = self.launch(timeout=0.15)
        self.assertEqual(channel.receive(3)[0], "READY")
        kind, message = channel.receive(3)
        self.assertEqual(kind, "ERROR")
        self.assertIn("timed out", message)
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])

    def test_watcher_eof_resumes(self):
        channel = self.launch()
        self.assertEqual(channel.receive(3)[0], "READY")
        channel.connection.close()
        self.wait_for(lambda: self.processes.history() == ["STOP", "CONT"])

    def test_caller_death_resumes_even_with_wrapper_connection_open(self):
        channel = self.launch(caller=self.processes.caller)
        self.assertEqual(channel.receive(3)[0], "READY")
        self.processes.caller_dead.touch()
        kind, message = channel.receive(3)
        self.assertEqual(kind, "ERROR")
        self.assertIn("caller exited", message)
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])

    def test_sigkill_of_wrapper_still_resumes(self):
        def worker():
            channel = guard.start_guardian(3, self.processes, self.lock_factory)
            channel.receive(3)
            time.sleep(10)

        pid = self.fork_worker(worker)
        self.wait_for(lambda: self.processes.history() == ["STOP"])
        os.kill(pid, signal.SIGKILL)
        self.assertEqual(self.wait_worker(pid), -signal.SIGKILL)
        self.wait_for(lambda: self.processes.history() == ["STOP", "CONT"])

    def test_watcher_does_not_inherit_sdk_pipes(self):
        read_fd, write_fd = os.pipe()
        try:
            channel = self.launch()
            self.assertEqual(channel.receive(3)[0], "READY")
            os.close(write_fd)
            write_fd = None
            self.assertTrue(select.select([read_fd], [], [], 0.5)[0])
            self.assertEqual(os.read(read_fd, 1), b"")
            self.assertEqual(self.release(channel)[0], "RESUMED")
        finally:
            os.close(read_fd)
            if write_fd is not None:
                os.close(write_fd)

    def test_resume_failure_is_explicit(self):
        self.processes.fail_resume = True
        with mock.patch.object(guard.syslog, "syslog"):
            channel = self.launch()
        self.assertEqual(channel.receive(3)[0], "READY")
        kind, message = self.release(channel)
        self.assertEqual(kind, "ERROR")
        self.assertIn("resume could not be verified", message)
        self.assertEqual(self.processes.history(), ["STOP"])

    def test_guardian_never_continues_reused_pid_after_stop(self):
        self.processes.changed_after_stop = True
        with mock.patch.object(guard.syslog, "syslog"):
            channel = self.launch()
        kind, message = channel.receive(3)
        self.assertEqual(kind, "ERROR")
        self.assertIn("PID/start-time identity changed", message)
        self.assertEqual(self.processes.history(), ["STOP"])

    def test_unexpected_guardian_error_resumes_without_success_acknowledgement(self):
        guardian_socket, client_socket = socket.socketpair()
        channel = guard.Channel(client_socket)
        self.channels.append(channel)
        with mock.patch.object(guard, "caller_alive",
                               side_effect=[True, RuntimeError("unexpected internal error")]):
            with self.assertRaisesRegex(RuntimeError, "unexpected internal error"):
                guard.guardian(guardian_socket, 3, self.processes, self.lock_factory)
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])
        self.assertEqual(channel.receive(3)[0], "READY")
        kind, message = channel.receive(3)
        self.assertEqual(kind, "ERROR")
        self.assertIn("Unexpected FileProvider guardian failure", message)
        self.assertNotIn("unexpected internal error", message)

    def test_unexpected_detached_guardian_error_fails_wrapper(self):
        errors = io.StringIO()
        with mock.patch.object(guard, "caller_alive",
                               side_effect=[True, RuntimeError("unexpected internal error")]), \
                mock.patch.object(guard, "run_command", return_value=0), \
                contextlib.redirect_stderr(errors):
            self.assertEqual(self.sign(), 1)
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])
        self.assertIn("Unexpected FileProvider guardian failure", errors.getvalue())

    def test_native_arguments_and_failure_status_preserved_and_daemon_resumed(self):
        arguments = ["-v", "--force", "--timestamp=none", "--sign", "identity with spaces",
                     "--entitlements", "/path with spaces/$literal;entitlements",
                     "/fake/Target.app"]
        commands = []

        def run(arguments, _channel):
            self.assertEqual(self.processes.history(), ["STOP"])
            commands.append(arguments)
            return 23 if arguments[0] == guard.CODESIGN else 0

        with mock.patch.object(guard, "run_command", side_effect=run):
            self.assertEqual(self.sign(arguments), 23)
        self.assertEqual(commands, [
            ["/usr/bin/xattr", "-d", "-r", "-s", "com.apple.FinderInfo", "/fake/Target.app"],
            ["/usr/bin/xattr", "-d", "-r", "-s", "com.apple.ResourceFork", "/fake/Target.app"],
            [guard.CODESIGN] + arguments])
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])

    def test_success_resumes(self):
        with mock.patch.object(guard, "run_command", return_value=0):
            self.assertEqual(self.sign(), 0)
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])

    def test_cleanup_failures_abort_signing_and_resume(self):
        for results, attribute in (([1], "FinderInfo"), ([0, 1], "ResourceFork")):
            self.events.write_text("")
            errors = io.StringIO()
            with mock.patch.object(guard, "run_command", side_effect=results) as run, \
                    contextlib.redirect_stderr(errors):
                self.assertEqual(self.sign(), 1)
            self.assertEqual(run.call_count, len(results))
            self.assertIn(attribute, errors.getvalue())
            self.assertEqual(self.processes.history(), ["STOP", "CONT"])

    def test_process_launch_failure_resumes_without_logging_signing_arguments(self):
        errors = io.StringIO()
        with mock.patch.object(guard, "run_command",
                               side_effect=OSError(errno.EIO, "Input/output error", "secret identity")), \
                contextlib.redirect_stderr(errors):
            self.assertEqual(self.sign(), 1)
        self.assertNotIn("secret", errors.getvalue())
        self.assertIn("Input/output", errors.getvalue())
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])

    def test_resume_failure_turns_success_into_failure(self):
        self.processes.fail_resume = True
        errors = io.StringIO()
        with mock.patch.object(guard, "run_command", return_value=0), \
                mock.patch.object(guard.syslog, "syslog"), contextlib.redirect_stderr(errors):
            self.assertEqual(self.sign(), 1)
        self.assertIn("resume could not be verified", errors.getvalue())

    def test_timeout_aborts_child_and_is_not_apparent_success(self):
        native_run = guard.run_command
        children = []
        native_popen = subprocess.Popen

        def popen(*args, **kwargs):
            child = native_popen(*args, **kwargs)
            children.append(child)
            return child

        def run(_arguments, channel):
            return native_run([sys.executable, "-c", "import time; time.sleep(10)"], channel)

        errors = io.StringIO()
        with mock.patch.object(guard, "run_command", side_effect=run), \
                mock.patch.object(guard.subprocess, "Popen", side_effect=popen), \
                contextlib.redirect_stderr(errors):
            self.assertEqual(self.sign(timeout=0.15), 1)
        self.assertIn("timed out", errors.getvalue())
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])
        self.assertTrue(children)
        self.assertTrue(all(child.poll() is not None for child in children))

    def test_timeout_acknowledgement_overrides_late_native_success(self):
        def run(arguments, channel):
            if arguments[0] == guard.CODESIGN:
                self.assertTrue(channel.readable(3))
                time.sleep(0.05)
            return 0

        errors = io.StringIO()
        with mock.patch.object(guard, "run_command", side_effect=run), \
                contextlib.redirect_stderr(errors):
            self.assertEqual(self.sign(timeout=0.15), 1)
        self.assertIn("timed out", errors.getvalue())
        self.assertEqual(self.processes.history(), ["STOP", "CONT"])

    def test_cancellation_signals_resume_and_preserve_signal_exit_status(self):
        native_run = guard.run_command
        for signum in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
            self.events.write_text("")

            def worker():
                def run(_arguments, channel):
                    return native_run([sys.executable, "-c", "import time; time.sleep(10)"], channel)
                with mock.patch.object(guard, "run_command", side_effect=run), \
                        contextlib.redirect_stderr(io.StringIO()):
                    return self.sign()

            pid = self.fork_worker(worker)
            self.wait_for(lambda: self.processes.history() == ["STOP"])
            os.kill(pid, signum)
            self.assertEqual(self.wait_worker(pid), 128 + signum)
            self.assertEqual(self.processes.history(), ["STOP", "CONT"])

    def test_cancellation_during_startup_waits_for_resume_acknowledgement(self):
        native_start = guard.start_guardian

        def worker():
            def start(*args, **kwargs):
                channel = native_start(*args, **kwargs)
                self.assertEqual(channel.receive(3)[0], "READY")
                os.kill(os.getpid(), signal.SIGTERM)
                return channel
            with mock.patch.object(guard, "start_guardian", side_effect=start), \
                    mock.patch.object(guard, "run_command") as run, \
                    contextlib.redirect_stderr(io.StringIO()):
                status = self.sign()
                run.assert_not_called()
                self.assertEqual(self.processes.history(), ["STOP", "CONT"])
                return status

        self.assertEqual(self.wait_worker(self.fork_worker(worker)), 128 + signal.SIGTERM)

    def test_actual_child_exit_and_signal_status(self):
        channel = self.launch()
        self.assertEqual(channel.receive(3)[0], "READY")
        self.assertEqual(guard.run_command(
            [sys.executable, "-c", "raise SystemExit(29)"], channel), 29)
        self.assertEqual(self.release(channel)[0], "RESUMED")
        self.events.write_text("")
        with mock.patch.object(guard, "run_command", side_effect=[0, 0, -signal.SIGTERM]):
            self.assertEqual(self.sign(), 128 + signal.SIGTERM)


if __name__ == "__main__":
    unittest.main()
