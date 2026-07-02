import argparse
import paramiko
import os
import sys
from dotenv import load_dotenv

# Load the environment variables from the .env file
load_dotenv()

def main():
    parser = argparse.ArgumentParser(description="20Hz Proxmox Telemetry Recorder")
    parser.add_argument("--vmid", required=True, help="Target VM or LXC ID (e.g., 100)")
    parser.add_argument("--name", required=True, help="Name of the run (used for the output file)")
    parser.add_argument("--type", default="qemu", choices=["qemu", "lxc"], help="Type of guest")

    # Pull defaults dynamically from the .env file
    parser.add_argument("--host", default=os.getenv("PROXMOX_HOST", "192.168.1.100"), help="Proxmox IP Address")
    parser.add_argument("--user", default=os.getenv("PROXMOX_USER", "root"), help="SSH Username")
    parser.add_argument("--password", default=os.getenv("PROXMOX_PASSWORD"), help="SSH Password")

    parser.add_argument("--hz", type=int, default=20, help="Sampling frequency")
    parser.add_argument("--stats-log", action="store_true",
                        help="Fetch /tmp/stats.log from VM serial console and merge inner_fps/outer_fps/to_spawn/... into each sample (default off)")
    args = parser.parse_args()

    if not args.password:
        print("[!] Error: PROXMOX_PASSWORD not found.")
        print("    Please set it in your .env file or pass it via --password.")
        sys.exit(1)

    remote_code = f"""
import time, json, sys, os, subprocess, select, re
vmid = "{args.vmid}"
vm_type = "{args.type}"
interval = 1.0 / {args.hz}
stats_log_enabled = {args.stats_log}

if vm_type == "qemu":
    cgroup_dir = f"/sys/fs/cgroup/qemu.slice/{{vmid}}.scope"
    net_iface = f"tap{{vmid}}i0"
else:
    cgroup_dir = f"/sys/fs/cgroup/lxc/{{vmid}}"
    net_iface = f"veth{{vmid}}i0"

# --- Serial console reader (qemu/VM only) ---
# Each microvm /init spawns an interactive sh on /dev/console; we attach to the
# QEMU serial socket via socat (same mechanism `qm terminal` uses) and pump
# `cat /tmp/stats.log` through it asynchronously. Each sample non-blockingly
# drains whatever the shell has produced since last call, caches the latest
# JSON, and queues another cat only once the previous one has been answered
# (or after 5*interval if it got lost). Prevents the recorder from going dead
# when a CPU-saturated VM stalls one response — we just use the cached fields
# until a fresh one lands. LXC has no serial socket; merge is skipped there.
serial_sock = f"/var/run/qemu-server/{{vmid}}.serial0"
console_proc = None
if stats_log_enabled and vm_type == "qemu" and os.path.exists(serial_sock):
    try:
        console_proc = subprocess.Popen(
            ["socat", "-", f"UNIX-CONNECT:{{serial_sock}}"],
            stdin=subprocess.PIPE, stdout=subprocess.PIPE,
            stderr=subprocess.DEVNULL, bufsize=0)
        # Wake the shell and drain its banner / motd.
        console_proc.stdin.write(b"\\n")
        console_proc.stdin.flush()
        time.sleep(0.3)
        try:
            while True:
                r, _, _ = select.select([console_proc.stdout], [], [], 0.05)
                if not r: break
                if not console_proc.stdout.read(4096): break
        except Exception:
            pass
    except Exception as e:
        sys.stderr.write(f"[recorder] could not open serial console: {{e}}\\n")
        console_proc = None

_json_re = re.compile(rb"\\{{\\s*\\\"t\\\".*?\\}}")
_last_stats = None
_last_cat_sent = 0.0
_last_response_at = 0.0

def grab_server_stats():
    global _last_stats, _last_cat_sent, _last_response_at
    if console_proc is None: return _last_stats
    try:
        # Non-blocking drain — take whatever the shell has produced since the
        # last call. Don't wait for THIS iteration's cat to come back; if the
        # VM is CPU-saturated the response may land 2-3 samples later, and
        # that's fine because we keep returning the cached stats meanwhile.
        buf = b""
        while True:
            r, _, _ = select.select([console_proc.stdout], [], [], 0)
            if not r: break
            chunk = console_proc.stdout.read(65536)
            if not chunk: break
            buf += chunk
        now = time.time()
        m = None
        for hit in _json_re.finditer(buf):
            m = hit
        if m is not None:
            try:
                _last_stats = json.loads(m.group(0).decode("utf-8", errors="ignore"))
                _last_response_at = now
            except Exception:
                pass
        # One-in-flight: only queue another cat once the previous one has been
        # answered, or after 5*interval if it got lost. Without this the shell's
        # command queue grows faster than it can drain under high CPU and the
        # responses we DO get end up stale — same symptom as "stays dead".
        responded = _last_response_at > _last_cat_sent
        timed_out = (now - _last_cat_sent) > 5.0 * interval
        if _last_cat_sent == 0.0 or responded or timed_out:
            try:
                console_proc.stdin.write(b"cat /tmp/stats.log\\n")
                console_proc.stdin.flush()
                _last_cat_sent = now
            except Exception:
                pass
        return _last_stats
    except Exception:
        return _last_stats

def read_int(p):
    try:
        with open(p, 'r') as f: return int(f.read().strip())
    except: return 0

def get_cpu():
    try:
        with open(f"{{cgroup_dir}}/cpu.stat", 'r') as f:
            for line in f:
                if line.startswith("usage_usec"): return int(line.split()[1])
    except: pass
    return 0

def get_net():
    rx = read_int(f"/sys/class/net/{{net_iface}}/statistics/rx_bytes")
    tx = read_int(f"/sys/class/net/{{net_iface}}/statistics/tx_bytes")
    return rx, tx

last_time = time.time()
last_cpu = get_cpu()
last_rx, last_tx = get_net()

while True:
    t = time.time()
    dt = t - last_time
    if dt >= interval:
        cur_cpu = get_cpu()
        cur_rx, cur_tx = get_net()
        mem = read_int(f"{{cgroup_dir}}/memory.current")

        cpu_p = ((cur_cpu - last_cpu) / (dt * 1000000.0)) * 100.0
        rx_r = (cur_rx - last_rx) / dt
        tx_r = (cur_tx - last_tx) / dt

        sample = {{"t": t, "c": cpu_p, "m": mem, "rx": rx_r, "tx": tx_r}}
        if stats_log_enabled:
            srv = grab_server_stats()
            if srv:
                for k in ("inner_fps", "outer_fps", "to_spawn", "spawned", "alive", "tick", "state"):
                    if k in srv: sample[k] = srv[k]

        print(json.dumps(sample))
        sys.stdout.flush()

        last_time, last_cpu, last_rx, last_tx = t, cur_cpu, cur_rx, cur_tx
    time.sleep(0.005)
"""

    os.makedirs("results", exist_ok=True)
    output_file = f"results/{args.name}.jsonl"

    print(f"[*] Starting {args.hz}Hz recording for VMID {args.vmid} ({args.type})")
    print(f"[*] Connecting to {args.host} as {args.user}...")

    # Initialize Paramiko SSH Client
    ssh = paramiko.SSHClient()
    ssh.set_missing_host_key_policy(paramiko.AutoAddPolicy())

    try:
        ssh.connect(
            hostname=args.host,
            username=args.user,
            password=args.password,
            timeout=5
        )
        print("[*] Connection established. Injecting telemetry script...")

        # Execute python3 and keep standard streams open
        stdin, stdout, stderr = ssh.exec_command("python3 -")

        # Feed the script into the remote Python interpreter
        stdin.write(remote_code)
        stdin.flush()
        stdin.channel.shutdown_write() # Tell remote interpreter EOF is reached for input

        print(f"[*] Saving telemetry stream to: {output_file}")
        print("[*] Press CTRL+C to stop recording...\n")

        with open(output_file, 'a') as f:
            for line in iter(stdout.readline, ''):
                sys.stdout.write(f"\r[Live] Writing: {line.strip()}          ")
                sys.stdout.flush()
                f.write(line)

    except paramiko.AuthenticationException:
        print("\n[!] Authentication failed. Check your password.")
    except Exception as e:
        print(f"\n[!] SSH Error: {e}")
    except KeyboardInterrupt:
        print(f"\n\n[+] Recording stopped. Data saved to {output_file}")
    finally:
        # Ensure the SSH connection is cleanly severed
        ssh.close()

if __name__ == "__main__":
    main()
