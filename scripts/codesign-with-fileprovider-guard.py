#!/usr/bin/python3
"""Keep FileProvider metadata out of one SDK codesign operation (target last)."""

import ctypes
import errno
import fcntl
import json
import math
import os
from pathlib import Path
import pwd
import re
import select
import signal
import socket
import stat
import subprocess
import sys
import syslog
import time
from collections import namedtuple


CODESIGN = "/usr/bin/codesign"
SERVICE = "com.apple.FileProvider"
DAEMON = "/System/Library/PrivateFrameworks/FileProvider.framework/Support/fileproviderd"
ATTRIBUTES = ("com.apple.FinderInfo", "com.apple.ResourceFork")
Process = namedtuple("Process", "pid uid start executable stopped")


class GuardError(Exception):
    pass


class Cancelled(BaseException):
    def __init__(self, signum):
        self.signum = signum


def user_home():
    # HOME can be overridden by a build. Use the account's actual home instead.
    return Path(pwd.getpwuid(os.getuid()).pw_dir).resolve()


def managed_target(arguments, home=None):
    signing = any(arg in ("--sign", "-s") or arg.startswith("--sign=")
                  or (arg.startswith("-s") and not arg.startswith("--"))
                  for arg in arguments[:-1])
    if not signing or not arguments:
        return None
    target = Path(arguments[-1]).resolve()
    root = ((home or user_home()) / "Library" / "CloudStorage").resolve()
    return target if root in target.parents or target == root else None


def timeout_seconds(environ):
    try:
        value = float(environ.get("FILEPROVIDER_CODESIGN_TIMEOUT_SECONDS", "60"))
    except ValueError:
        value = float("nan")
    if not math.isfinite(value) or not 1 <= value <= 300:
        raise GuardError("FILEPROVIDER_CODESIGN_TIMEOUT_SECONDS must be between 1 and 300.")
    return value


def open_lock(home=None):
    home = home or user_home()
    directory = home / "Library" / "Caches" / "SeattleCarsInBikeLanes.CodesignGuard"
    root = (home / "Library" / "CloudStorage").resolve()
    if directory.resolve() == root or root in directory.resolve().parents:
        raise GuardError("The signing guard's cache directory must be outside CloudStorage.")
    directory.mkdir(mode=0o700, parents=True, exist_ok=True)
    info = directory.lstat()
    if not stat.S_ISDIR(info.st_mode) or info.st_uid != os.getuid() or info.st_mode & 0o077:
        raise GuardError("The signing guard's cache directory must be private and user-owned.")
    fd = os.open(str(directory / "fileprovider.lock"),
                 os.O_CREAT | os.O_RDWR | os.O_NOFOLLOW, 0o600)
    info = os.fstat(fd)
    if (not stat.S_ISREG(info.st_mode) or info.st_uid != os.getuid()
            or info.st_mode & 0o077 or info.st_nlink != 1):
        os.close(fd)
        raise GuardError("The signing guard's lock must be a private, user-owned regular file.")
    # Never unlink: all contenders must flock the same inode.
    return os.fdopen(fd, "r+b")


class BSDInfo(ctypes.Structure):
    # Darwin's proc_bsdinfo (PROC_PIDTBSDINFO); start time includes microseconds.
    _fields_ = ([(name, ctypes.c_uint32) for name in (
        "flags", "status", "xstatus", "pid", "ppid", "uid", "gid", "ruid",
        "rgid", "svuid", "svgid", "reserved")]
        + [("comm", ctypes.c_char * 16), ("name", ctypes.c_char * 32)]
        + [(name, ctypes.c_uint32) for name in (
            "nfiles", "pgid", "jobc", "tdev", "tpgid", "nice")]
        + [("start_sec", ctypes.c_uint64), ("start_usec", ctypes.c_uint64)])


class DarwinProcesses:
    def __init__(self):
        self.lib = ctypes.CDLL("/usr/lib/libproc.dylib", use_errno=True)
        self.lib.proc_pidinfo.argtypes = (
            ctypes.c_int, ctypes.c_int, ctypes.c_uint64, ctypes.c_void_p, ctypes.c_int)
        self.lib.proc_pidinfo.restype = ctypes.c_int
        self.lib.proc_pidpath.argtypes = (ctypes.c_int, ctypes.c_void_p, ctypes.c_uint32)
        self.lib.proc_pidpath.restype = ctypes.c_int

    def service_pid(self):
        result = subprocess.run(
            ["/bin/launchctl", "list", SERVICE], capture_output=True, text=True, timeout=5)
        labels = re.findall(r'^\s*"Label"\s*=\s*"([^"]+)";\s*$', result.stdout, re.M)
        pids = re.findall(r'^\s*"PID"\s*=\s*(\d+);\s*$', result.stdout, re.M)
        if result.returncode or labels != [SERVICE] or len(pids) != 1 or int(pids[0]) <= 1:
            raise GuardError("Cannot identify the current user's com.apple.FileProvider service. "
                             "Check the login session, or build outside CloudStorage.")
        return int(pids[0])

    def _info(self, pid):
        info = BSDInfo()
        size = self.lib.proc_pidinfo(pid, 3, 0, ctypes.byref(info), ctypes.sizeof(info))
        if size != ctypes.sizeof(info):
            if size == 0 and ctypes.get_errno() == errno.ESRCH:
                return None
            raise GuardError("Cannot inspect process identity; check process permissions.")
        return info

    def process(self, pid):
        before = self._info(pid)
        if before is None:
            return None
        path = ctypes.create_string_buffer(4096)
        if self.lib.proc_pidpath(pid, path, len(path)) <= 0:
            if ctypes.get_errno() == errno.ESRCH:
                return None
            raise GuardError("Cannot verify process executable; check process permissions.")
        after = self._info(pid)
        if after is None:
            return None
        if ((before.pid, before.uid, before.start_sec, before.start_usec)
                != (after.pid, after.uid, after.start_sec, after.start_usec)):
            raise GuardError("Process identity changed during inspection; refusing to signal.")
        return Process(after.pid, after.uid, (after.start_sec, after.start_usec),
                       os.fsdecode(path.value), after.status == 4)

    def send_signal(self, pid, signum):
        os.kill(pid, signum)


def same_process(left, right):
    return left is not None and right is not None and left[:4] == right[:4]


def checked_process(processes, expected):
    current = processes.process(expected.pid)
    if not same_process(current, expected):
        raise GuardError("FileProvider PID/start-time identity changed; refusing to signal.")
    return current


def identify_provider(processes):
    pid = processes.service_pid()
    provider = processes.process(pid)
    if (provider is None or provider.pid != pid or provider.pid <= 1 or provider.uid != os.getuid()
            # Normalize only the trusted OS path; libproc's executable stays exact.
            or provider.executable != os.path.realpath(DAEMON)):
        raise GuardError("FileProvider executable or user ownership could not be verified.")
    if provider.stopped:
        raise GuardError("FileProvider is already suspended; refusing to take over another pause.")
    return provider


def wait_state(processes, provider, stopped, deadline):
    while checked_process(processes, provider).stopped != stopped:
        if time.monotonic() >= deadline:
            raise GuardError("Could not verify FileProvider's stopped/resumed state.")
        time.sleep(0.01)


def resume_provider(processes, provider):
    error = None
    for _ in range(3):
        try:
            checked_process(processes, provider)
            # CONT also cancels a pending STOP on a process stuck in kernel I/O.
            processes.send_signal(provider.pid, signal.SIGCONT)
            wait_state(processes, provider, False, time.monotonic() + 0.5)
            return
        except (GuardError, OSError) as exc:
            error = exc
            time.sleep(0.1)
    raise GuardError("FileProvider resume could not be verified. Inspect the login service "
                     "before retrying. " + safe_error(error))


def safe_error(error):
    if isinstance(error, GuardError):
        return str(error)
    if isinstance(error, OSError):
        return error.strerror or "Operating system operation failed."
    return "Signing guard operation failed."


class Channel:
    def __init__(self, connection):
        self.connection = connection
        self.buffer = b""
        self.final = None

    def send(self, kind, message=""):
        self.connection.sendall(json.dumps([kind, message]).encode("utf-8") + b"\n")

    def receive(self, timeout):
        deadline = time.monotonic() + timeout
        while b"\n" not in self.buffer:
            if not select.select([self.connection], [], [], max(0, deadline - time.monotonic()))[0]:
                raise GuardError("Timed out waiting for the detached FileProvider guardian.")
            data = self.connection.recv(4096)
            if not data:
                raise GuardError("FileProvider guardian disconnected without a resume acknowledgement.")
            self.buffer += data
        line, self.buffer = self.buffer.split(b"\n", 1)
        result = json.loads(line)
        if result[0] != "READY":
            self.final = result
        return result

    def readable(self, timeout=0):
        return b"\n" in self.buffer or bool(select.select([self.connection], [], [], timeout)[0])


def caller_alive(processes, caller):
    return caller is None or same_process(processes.process(caller.pid), caller)


def guardian(connection, timeout, processes, lock_factory=open_lock, caller=None):
    """Only this detached process owns the lock and signals the provider."""
    channel = Channel(connection)
    provider = None
    resume_needed = False
    completed = False
    result = ("ERROR", "Unexpected FileProvider guardian failure; signing was aborted.")
    try:
        with lock_factory() as lock:
            try:
                deadline = time.monotonic() + timeout
                while True:
                    if channel.readable():
                        completed = True
                        return
                    if not caller_alive(processes, caller):
                        raise GuardError("The codesign caller exited; signing was aborted.")
                    try:
                        fcntl.flock(lock, fcntl.LOCK_EX | fcntl.LOCK_NB)
                        break
                    except BlockingIOError:
                        if time.monotonic() >= deadline:
                            raise GuardError("Timed out waiting for another FileProvider signer.")
                        time.sleep(0.05)
                provider = identify_provider(processes)
                # Establish the watchdog and cleanup scope before the first STOP.
                deadline = time.monotonic() + timeout
                if checked_process(processes, provider).stopped:
                    raise GuardError("FileProvider became suspended; refusing to take over another pause.")
                try:
                    processes.send_signal(provider.pid, signal.SIGSTOP)
                except OSError as exc:
                    raise GuardError("Could not pause verified FileProvider: " + safe_error(exc)
                                     + ". Check login-session permissions or build outside CloudStorage.")
                resume_needed = True
                wait_state(processes, provider, True, min(deadline, time.monotonic() + 2))
                channel.send("READY")
                while True:
                    if time.monotonic() >= deadline:
                        raise GuardError("FileProvider signing guard timed out; signing was aborted.")
                    if not caller_alive(processes, caller):
                        raise GuardError("The codesign caller exited; signing was aborted.")
                    if not checked_process(processes, provider).stopped:
                        raise GuardError("FileProvider resumed unexpectedly during signing.")
                    if channel.readable(min(0.05, max(0, deadline - time.monotonic()))):
                        # EOF covers wrapper SIGKILL. A byte requests normal release.
                        connection.recv(1)
                        break
                completed = True
            except (GuardError, OSError, subprocess.TimeoutExpired) as exc:
                result = ("ERROR", safe_error(exc))
            finally:
                try:
                    if resume_needed:
                        resume_provider(processes, provider)
                    if completed:
                        result = ("RESUMED", "")
                except (GuardError, OSError) as exc:
                    result = ("ERROR", safe_error(exc))
                    syslog.syslog(syslog.LOG_ERR, "FileProvider codesign guard: " + result[1])
                finally:
                    # The lock stays held through verified resume and acknowledgement.
                    try:
                        channel.send(*result)
                    except OSError:
                        pass
    except (GuardError, OSError, subprocess.TimeoutExpired) as exc:
        try:
            channel.send("ERROR", safe_error(exc))
        except OSError:
            pass
    finally:
        connection.close()


def detach_descriptors(keep_fd):
    null = os.open(os.devnull, os.O_RDWR)
    for fd in (0, 1, 2):
        os.dup2(null, fd)
    # Close SDK pipes and all other inherited descriptors, not just standard I/O.
    for name in os.listdir("/dev/fd"):
        fd = int(name)
        if fd > 2 and fd != keep_fd:
            try:
                os.close(fd)
            except OSError:
                pass


def start_guardian(timeout, processes, lock_factory=open_lock, caller=None):
    parent, child = socket.socketpair()
    try:
        pid = os.fork()
    except BaseException:
        parent.close()
        child.close()
        raise
    if pid == 0:
        parent.close()
        try:
            for signum in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
                signal.signal(signum, signal.SIG_IGN)
            os.setsid()
            intermediate = os.getpid()
            if os.fork() != 0:
                os._exit(0)
            while os.getppid() == intermediate:
                time.sleep(0.001)
            detach_descriptors(child.fileno())
            guardian(child, timeout, processes, lock_factory, caller)
        finally:
            os._exit(0)
    child.close()
    try:
        os.waitpid(pid, 0)
    except BaseException:
        parent.close()
        raise
    return Channel(parent)


def run_command(arguments, channel):
    process = subprocess.Popen(arguments, close_fds=True)
    try:
        while process.poll() is None:
            if channel.readable(0.05):
                kind, message = channel.receive(1)
                raise GuardError(message or "FileProvider guardian ended before signing completed.")
        return process.returncode
    finally:
        if process.poll() is None:
            try:
                process.terminate()
            except ProcessLookupError:
                pass
            try:
                process.wait(timeout=2)
            except subprocess.TimeoutExpired:
                process.kill()
                process.wait(timeout=2)


def guarded_sign(arguments, target, timeout, processes, lock_factory=open_lock, caller=None):
    channel = None
    saved = {}
    status = 1
    error = None
    starting = True
    pending_cancel = None

    def cancel(signum, _frame):
        nonlocal pending_cancel
        if starting:
            pending_cancel = signum
        else:
            raise Cancelled(signum)

    try:
        for signum in (signal.SIGINT, signal.SIGTERM, signal.SIGHUP):
            saved[signum] = signal.signal(signum, cancel)
        channel = start_guardian(timeout, processes, lock_factory, caller)
        starting = False
        if pending_cancel:
            raise Cancelled(pending_cancel)
        kind, message = channel.receive(timeout + 10)
        if kind != "READY":
            raise GuardError(message or "FileProvider guardian could not be armed.")
        for attribute in ATTRIBUTES:
            if run_command(["/usr/bin/xattr", "-d", "-r", "-s", attribute, str(target)], channel):
                raise GuardError("Could not remove " + attribute + " from the signing target.")
        status = run_command([CODESIGN] + list(arguments), channel)
    except Cancelled as exc:
        status = 128 + exc.signum
        error = "Signing cancelled."
    except (GuardError, OSError) as exc:
        error = safe_error(exc)
    finally:
        # Repeated cancellation must not interrupt the mandatory resume handshake.
        for signum in saved:
            signal.signal(signum, signal.SIG_IGN)
        if channel is not None:
            try:
                if channel.final is None:
                    try:
                        channel.connection.sendall(b"D")
                    except (BrokenPipeError, ConnectionResetError):
                        # A timeout/error acknowledgement may already be queued.
                        pass
                    kind, message = channel.receive(timeout + 10)
                    # Cancellation during startup can leave READY ahead of the acknowledgement.
                    if kind == "READY":
                        kind, message = channel.receive(timeout + 10)
                else:
                    kind, message = channel.final
                if kind != "RESUMED":
                    raise GuardError(message or "FileProvider resume was not acknowledged.")
            except (GuardError, OSError) as exc:
                error = safe_error(exc)
                if isinstance(exc, OSError):
                    error = "Could not obtain FileProvider resume acknowledgement: " + error
                status = 1
            finally:
                channel.connection.close()
        for signum, handler in saved.items():
            signal.signal(signum, handler)
    if error:
        print("FileProvider codesign guard: " + error, file=sys.stderr)
    return status if status >= 0 else 128 - status


def main(arguments=None):
    arguments = sys.argv[1:] if arguments is None else arguments
    try:
        target = managed_target(arguments)
        if target is None:
            os.execv(CODESIGN, [CODESIGN] + list(arguments))
        processes = DarwinProcesses()
        caller_pid = os.getppid()
        caller = processes.process(caller_pid) if caller_pid > 1 else None
        if caller_pid > 1 and caller is None:
            raise GuardError("The codesign caller exited before the guard was armed.")
        return guarded_sign(arguments, target, timeout_seconds(os.environ), processes, caller=caller)
    except (GuardError, OSError, subprocess.TimeoutExpired) as exc:
        print("FileProvider codesign guard: " + safe_error(exc), file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
