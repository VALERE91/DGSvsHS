"""
Orchestrates an automated benchmark suite:

  1. Loops N times based on the --runs flag.
  2. If --restart is set, SSH into Proxmox, restart the VM, and wait 20s for boot.
  3. Start record_run.py against the chosen Proxmox VM.
  4. Launch Unity client .exe processes with autopilot (--bot-id i).
  5. Hold the active benchmark state for a set duration (default 4m30s).
  6. Terminate the clients.
  7. Hold a cooldown window (default 30s) to capture post-disconnect idle.
  8. Safely terminate the recorder.
  9. Repeat for the next run iteration.

Output files are automatically suffixed: results/<case>_1.jsonl, <case>_2.jsonl, etc.

Usage examples:
    python run_case.py --case dgs_baseline    --flavor dgs    --vmid 202 --runs 5
    python run_case.py --case bevy_stress     --flavor bevy   --vmid 200 --runs 3 --bots 4 -r
    python run_case.py --case unreal_baseline --flavor unreal --vmid 203 --runs 5

Per-flavor client exe is read from the .env file as CLIENT_EXE_<FLAVOR>
(e.g. CLIENT_EXE_UNREAL=/path/to/UnrealvsHS.exe). All client builds accept
the same `--bot-id N` two-arg CLI: the Unreal client parses Unity-style
long-flag form alongside UE's native `-key=value` form (UvHSClientGameMode.cpp).
"""

import argparse
import os
import signal
import subprocess
import sys
import time
import paramiko
from pathlib import Path

from dotenv import load_dotenv

HERE = Path(__file__).resolve().parent
load_dotenv(HERE / ".env")

WIN = sys.platform == "win32"


def env(key: str, default=None, required: bool = False) -> str:
    v = os.getenv(key, default)
    if required and not v:
        sys.exit(f"[!] missing required env var: {key} (set in {HERE / '.env'})")
    return v


def resolve_exe(flavor: str) -> str:
    key = f"CLIENT_EXE_{flavor.upper()}"
    path = env(key, required=True)
    if not Path(path).is_file():
        sys.exit(f"[!] {key} points at non-existent file: {path}")
    return path


# Per-server UDP listen ports (CLAUDE.md §11.1):
#   DGS    7777  — Unity DOTS server
#   Arch   7778  — C#/Arch server
#   Bevy   4433  — Rust/Bevy server
#   Unreal 7780  — UE/Mass server (init.sh launches with -QuicPort=7780)
FLAVOR_PORT = {"dgs": 7777, "arch": 7778, "bevy": 4433, "unreal": 7780}


def restart_vm(vmid: str, vm_type: str) -> None:
    host = env("PROXMOX_HOST", default="192.168.1.100")
    user = env("PROXMOX_USER", default="root")
    password = env("PROXMOX_PASSWORD", required=True)

    print(f"[harness] Restarting {vm_type} {vmid} on {host}...")
    
    # Handle qm for VMs and pct for LXC containers
    cmd_stop = f"qm stop {vmid}" if vm_type == "qemu" else f"pct stop {vmid}"
    cmd_start = f"qm start {vmid}" if vm_type == "qemu" else f"pct start {vmid}"

    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())
    
    try:
        ssh.connect(host, username=user, password=password, timeout=10)
        
        print(f"[harness] Stopping guest: {cmd_stop}")
        _, stdout, _ = ssh.exec_command(cmd_stop)
        stdout.channel.recv_exit_status()  # Block until stop command completes
        
        print(f"[harness] Starting guest: {cmd_start}")
        _, stdout, _ = ssh.exec_command(cmd_start)
        stdout.channel.recv_exit_status()  # Block until start command completes
        
    except paramiko.AuthenticationException:
        sys.exit("\n[!] Authentication failed. Check your PROXMOX_PASSWORD in .env.")
    except Exception as e:
        sys.exit(f"\n[!] SSH Error during restart: {e}")
    finally:
        ssh.close()
        
    print("[harness] Guest started successfully.")
    countdown_timer(60.0, phase="VM Boot")


def start_recorder(case: str, vmid: str, vm_type: str, hz: int) -> subprocess.Popen:
    cmd = [
        sys.executable,
        str(HERE / "record_run.py"),
        "--vmid", vmid,
        "--name", case,
        "--type", vm_type,
        "--hz", str(hz),
    ]
    print(f"[harness] starting recorder: {' '.join(cmd)}")
    kwargs = {}
    if WIN:
        kwargs["creationflags"] = subprocess.CREATE_NEW_PROCESS_GROUP
    return subprocess.Popen(cmd, **kwargs)


def stop_recorder(proc: subprocess.Popen, grace_sec: float = 5.0) -> None:
    print("[harness] signaling recorder to stop (SSH cleanup)…")
    try:
        if WIN:
            proc.send_signal(signal.CTRL_BREAK_EVENT)
        else:
            proc.send_signal(signal.SIGINT)
    except Exception as e:
        print(f"[harness] recorder signal failed: {e}")
    try:
        proc.wait(timeout=grace_sec)
        print(f"[harness] recorder exited cleanly (rc={proc.returncode})")
    except subprocess.TimeoutExpired:
        print(f"[harness] recorder didn't stop in {grace_sec}s — killing")
        proc.kill()
        proc.wait(timeout=2.0)


def start_clients(exe: str, bots: int, flavor: str, duration_sec: float) -> list[subprocess.Popen]:
    port = FLAVOR_PORT[flavor]
    procs: list[subprocess.Popen] = []
    for i in range(bots):
        cmd = [
            exe,
            "--bot-id",   str(i),
            "--port",     str(port),
            "--duration", str(duration_sec),
        ]
        print(f"[harness] launching client {i}: {' '.join(cmd)}")
        p = subprocess.Popen(cmd)
        procs.append(p)
        time.sleep(0.5)
    return procs


def stop_clients(procs: list[subprocess.Popen], grace_sec: float = 5.0) -> None:
    # UE5 packaged Windows clients launch a top-level bootstrap .exe that
    # CreateProcess-spawns the real game from Binaries/Win64/<Project>.exe.
    # Plain proc.terminate() (TerminateProcess) only kills the bootstrap; the
    # inner game gets orphaned and keeps running. taskkill /F /T walks the
    # full process tree so the actual game dies too. Unity standalones don't
    # do this nesting but taskkill /T is harmless on them.
    print(f"[harness] terminating {len(procs)} client(s)…")
    for p in procs:
        try:
            if WIN:
                subprocess.run(
                    ["taskkill", "/F", "/T", "/PID", str(p.pid)],
                    stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL,
                    check=False,
                )
            else:
                p.terminate()
        except Exception:
            pass
    for p in procs:
        try:
            p.wait(timeout=grace_sec)
        except subprocess.TimeoutExpired:
            print(f"[harness] client pid={p.pid} didn't exit in {grace_sec}s — killing")
            p.kill()
            p.wait(timeout=2.0)


def countdown_timer(seconds: float, phase: str) -> None:
    """Displays a live countdown timer that can be cleanly interrupted."""
    end = time.time() + seconds
    try:
        while True:
            remaining = end - time.time()
            if remaining <= 0:
                break
            sys.stdout.write(f"\r[harness] {phase} timer: {remaining:5.1f}s remaining  ")
            sys.stdout.flush()
            time.sleep(min(0.2, remaining))
        sys.stdout.write(f"\r[harness] {phase} phase complete.                     \n")
    except KeyboardInterrupt:
        # Re-raise so the main loop can catch it and handle cleanup before exiting
        raise


def parse_args() -> argparse.Namespace:
    ap = argparse.ArgumentParser(description="Automated multi-run benchmark orchestrator")
    ap.add_argument("--case", required=True, help="Base name of the run (creates results/<case>_X.jsonl)")
    ap.add_argument("--flavor", required=True, choices=["dgs", "arch", "bevy", "unreal"], help="Client/server flavor")

    # Automation flags
    ap.add_argument("-r", "--restart", action="store_true", help="Restart the Proxmox VM and wait 20s before each run")
    ap.add_argument("--runs", type=int, default=1, help="Number of sequential runs to execute")
    ap.add_argument("--duration-sec", type=float, default=300.0, help="Duration of active benchmark per run in seconds (default 270s / 4m30s)")
    ap.add_argument("--cooldown-sec", type=float, default=30.0, help="Duration of post-disconnect idle capture (default 30s)")

    ap.add_argument("--bots", type=int, default=2, help="Number of autopilot client processes")
    ap.add_argument("--vmid", default="901", help="Proxmox VMID for record_run.py")
    ap.add_argument("--type", default="qemu", choices=["qemu", "lxc"], help="Proxmox guest type")
    ap.add_argument("--hz", type=int, default=20, help="Telemetry sampling Hz")

    args = ap.parse_args()
    if not (1 <= args.bots <= 4):
        sys.exit("[!] --bots must be 1..4")
    if args.runs < 1:
        sys.exit("[!] --runs must be at least 1")
    return args


def main() -> int:
    args = parse_args()
    exe = resolve_exe(args.flavor)
    (HERE / "results").mkdir(exist_ok=True)

    print("=" * 60)
    print(f"  Suite: case={args.case} | flavor={args.flavor} | runs={args.runs} | bots={args.bots}")
    print(f"  Timing: duration={args.duration_sec}s | cooldown={args.cooldown_sec}s")
    if args.restart:
        print("  Flag: VM Restart BEFORE each run enabled (-r)")
    print("=" * 60)

    try:
        for run_idx in range(1, args.runs + 1):
            current_case_name = f"{args.case}_{run_idx}"
            print(f"\n>>> Starting run {run_idx}/{args.runs}: {current_case_name}")

            # 1. Restart VM if the flag was passed
            if args.restart:
                restart_vm(args.vmid, args.type)

            # 2. Start Telemetry
            recorder = start_recorder(current_case_name, args.vmid, args.type, args.hz)
            time.sleep(3.0) # Warm-up for SSH stream establishment

            if recorder.poll() is not None:
                sys.exit(f"[!] recorder failed to start (rc={recorder.returncode})")

            # 3. Launch Clients
            clients = start_clients(exe, args.bots, args.flavor, args.duration_sec)

            try:
                # Active Phase
                countdown_timer(args.duration_sec, phase="Active")
            finally:
                # Always kill clients, even if operator aborts active phase
                stop_clients(clients)

            try:
                # Cooldown Phase
                countdown_timer(args.cooldown_sec, phase="Cooldown")
            finally:
                # Always kill recorder, even if operator aborts cooldown
                stop_recorder(recorder)

            out = HERE / "results" / f"{current_case_name}.jsonl"
            if out.is_file():
                print(f"[harness] Run {run_idx} saved: {out.name} ({(out.stat().st_size / 1024):.1f} KB)")
            else:
                print(f"[!] Warning: No output file found for run {run_idx} at {out}")

            # Small breather between full runs to ensure ports and sockets close out completely
            if run_idx < args.runs:
                time.sleep(2.0)

    except KeyboardInterrupt:
        print("\n\n[!] Operator aborted the suite entirely. Exiting.")
        return 1

    print("\n" + "=" * 60)
    print(f"[harness] Suite complete. {args.runs} runs saved to results/")
    print("=" * 60)
    return 0


if __name__ == "__main__":
    sys.exit(main())