import os
import json
import re
import numpy as np
import matplotlib.pyplot as plt
from collections import defaultdict

# --- Configuration ---
RESULTS_DIR = "results"
OUTPUT_DIR = "graphs"

# Fenêtre de lissage pour la moyenne glissante. 
# À 20Hz : 10 = 0.5s de lissage, 20 = 1s de lissage, 40 = 2s de lissage.
SMOOTHING_WINDOW = 10

# Y-axis outlier clipping. Transient startup spikes (e.g. Bevy's first-frame
# ~46% CPU blip) otherwise squash the whole chart into the bottom. If the true
# max exceeds the "robust top" (max across series of their Nth percentile) by
# YLIM_OUTLIER_FACTOR, cap the y-axis there (+ headroom) so the spike runs off
# the top instead of compressing everything. Normal charts (no outlier) keep
# matplotlib's auto-scaling untouched.
YLIM_PERCENTILE     = 99.0
YLIM_OUTLIER_FACTOR = 1.3
YLIM_HEADROOM       = 1.15

# Display configurations
METRICS = {
    'c': {'title': 'CPU Usage', 'ylabel': 'CPU Utilization (%)', 'scale': 1.0},
    'm': {'title': 'Memory Allocation', 'ylabel': 'Memory (MB)', 'scale': 1 / (1024 * 1024)},
    'rx': {'title': 'Network Ingress (Rx)', 'ylabel': 'Data Rate (KB/s)', 'scale': 1 / 1024},
    'tx': {'title': 'Network Egress (Tx)', 'ylabel': 'Data Rate (KB/s)', 'scale': 1 / 1024},
    'inner_fps': {'title': 'Sim FPS (Inner)', 'ylabel': 'Frames per Second', 'scale': 1.0},
    'outer_fps': {'title': 'Update FPS (Outer)', 'ylabel': 'Frames per Second', 'scale': 1.0},
}
FLAVOR_COLORS     = {'dgs': '#1f77b4', 'arch': '#ff7f0e', 'bevy': '#2ca02c', 'unreal': '#d62728'} # Blue,    Orange,    Green,   Red       — CPU / single-metric plots
FLAVOR_FPS_COLORS = {'dgs': '#0d3b66', 'arch': '#a04000', 'bevy': '#145214', 'unreal': '#7a1212'} # Navy,    BurntOrng, ForestGrn, Maroon  — FPS line on CPU-vs-FPS overlays (same family, darker)

# Human-readable display labels (legend, titles, filenames). Internal keys stay
# lowercase ('dgs') because they're parsed from filenames and key the color
# dicts; only the presentation string changes.
FLAVOR_LABELS = {'dgs': 'Unity', 'arch': 'ARCH', 'bevy': 'Bevy', 'unreal': 'Unreal'}

def flavor_label(flavor):
    return FLAVOR_LABELS.get(flavor, flavor.upper())

# Draw order for overlapping lines on multi-flavor plots: higher zorder = on top.
# Green (bevy) drawn last/on top, red (unreal) at the bottom, per request.
# Values start at 2 so lines stay above the grid.
FLAVOR_ZORDER = {'unreal': 2, 'arch': 3, 'dgs': 4, 'bevy': 5}
# ---------------------

def parse_files():
    """Reads all JSONL files and organizes them by flavor and run number.
    Missing fields (e.g. inner_fps in pre-2026-06 runs) are stored as NaN so
    averaging via np.nanmean ignores them per-column instead of crashing."""
    fields = ['t'] + list(METRICS.keys())
    data = defaultdict(lambda: defaultdict(lambda: {m: [] for m in fields}))
    run_numbers = set()

    if not os.path.exists(RESULTS_DIR):
        print(f"[!] Directory '{RESULTS_DIR}' not found.")
        return None, None

    file_pattern = re.compile(r'(?i)(dgs|arch|bevy|unreal).*?_(\d+)\.jsonl$')

    for filename in os.listdir(RESULTS_DIR):
        match = file_pattern.match(filename)
        if match:
            flavor = match.group(1).lower()
            run_num = int(match.group(2))
            run_numbers.add(run_num)

            filepath = os.path.join(RESULTS_DIR, filename)
            with open(filepath, 'r') as f:
                start_time = None
                for line in f:
                    try:
                        row = json.loads(line.strip())
                    except json.JSONDecodeError:
                        continue
                    if 't' not in row:
                        continue
                    if start_time is None:
                        start_time = row['t']

                    data[flavor][run_num]['t'].append(row['t'] - start_time)
                    for m, m_info in METRICS.items():
                        v = row.get(m)
                        data[flavor][run_num][m].append(
                            float('nan') if v is None else v * m_info['scale']
                        )

    return data, sorted(list(run_numbers))

def smooth_data(y, window_size):
    """Applique une moyenne glissante simple en utilisant numpy.
    NaN-aware: NaN points are dropped from the kernel window per-position."""
    if window_size < 2 or len(y) < window_size:
        return y
    arr = np.asarray(y, dtype=float)
    mask = (~np.isnan(arr)).astype(float)
    filled = np.where(np.isnan(arr), 0.0, arr)
    num = np.convolve(filled, np.ones(window_size), mode='same')
    den = np.convolve(mask,    np.ones(window_size), mode='same')
    with np.errstate(invalid='ignore', divide='ignore'):
        out = np.where(den > 0, num / den, np.nan)
    return out

def robust_ylim_top(series_list):
    """Y-axis top that hides narrow transient spikes but keeps sustained peaks.

    Takes the max across series of each series' YLIM_PERCENTILE percentile (a
    sustained peak sits at/below its own p99; a narrow spike sits well above it).
    Returns that value * headroom ONLY if the real max exceeds it by
    YLIM_OUTLIER_FACTOR — otherwise None, meaning 'no outlier, auto-scale'."""
    pctls, maxes = [], []
    for s in series_list:
        arr = np.asarray(s, dtype=float)
        arr = arr[np.isfinite(arr)]
        if arr.size == 0:
            continue
        pctls.append(np.percentile(arr, YLIM_PERCENTILE))
        maxes.append(arr.max())
    if not pctls:
        return None
    robust = max(pctls)
    if robust > 0 and max(maxes) > robust * YLIM_OUTLIER_FACTOR:
        return robust * YLIM_HEADROOM
    return None

def plot_graph(x_data_dict, y_data_dict, title, ylabel, filename):
    """Utility to render and save a single SVG."""
    plt.figure(figsize=(12, 6))
    
    has_data = False
    smoothed_series = []
    for flavor, y_vals in y_data_dict.items():
        if y_vals is not None and len(y_vals) > 0:
            x_vals = x_data_dict[flavor]
            color = FLAVOR_COLORS.get(flavor, '#333333')

            # Application du lissage avant de tracer
            y_smoothed = smooth_data(y_vals, SMOOTHING_WINDOW)

            plt.plot(x_vals, y_smoothed, label=flavor_label(flavor), color=color, linewidth=1.5, alpha=0.85,
                     zorder=FLAVOR_ZORDER.get(flavor, 2))
            smoothed_series.append(y_smoothed)
            has_data = True

    if not has_data:
        plt.close()
        return

    # Clip the y-axis if a transient spike dwarfs the sustained signal.
    top = robust_ylim_top(smoothed_series)
    if top is not None:
        plt.ylim(top=top)

    plt.title(title, fontsize=14, fontweight='bold')
    plt.xlabel("Elapsed Time (Seconds)", fontsize=11)
    plt.ylabel(ylabel, fontsize=11)
    plt.grid(True, linestyle=':', alpha=0.7)
    plt.legend(loc="upper right")
    plt.tight_layout()
    
    plt.savefig(filename, format='svg')
    plt.close()

def plot_cpu_vs_fps(x_dict, cpu_dict, fps_dict, title, filename, fps_label='Sim FPS'):
    """Dual-axis overlay: CPU% on the left, FPS on the right, one solid + one
    dashed line per flavor. Lets the eye correlate cost vs throughput across
    flavors on the same time base."""
    has_any = any(
        (cpu_dict.get(f) is not None and len(cpu_dict[f]) > 0) or
        (fps_dict.get(f) is not None and len(fps_dict[f]) > 0)
        for f in x_dict
    )
    if not has_any:
        return

    fig, ax_cpu = plt.subplots(figsize=(12, 6))
    ax_fps = ax_cpu.twinx()

    cpu_smoothed = []
    for flavor, x_vals in x_dict.items():
        cpu_color = FLAVOR_COLORS.get(flavor, '#333333')
        fps_color = FLAVOR_FPS_COLORS.get(flavor, '#777777')
        cpu_vals = cpu_dict.get(flavor)
        fps_vals = fps_dict.get(flavor)
        if cpu_vals is not None and len(cpu_vals) > 0:
            cpu_s = smooth_data(cpu_vals, SMOOTHING_WINDOW)
            ax_cpu.plot(x_vals, cpu_s,
                        label=f"{flavor_label(flavor)} CPU", color=cpu_color,
                        linestyle='-', linewidth=1.5, alpha=0.85)
            cpu_smoothed.append(cpu_s)
        if fps_vals is not None and len(fps_vals) > 0:
            ax_fps.plot(x_vals, smooth_data(fps_vals, SMOOTHING_WINDOW),
                        label=f"{flavor_label(flavor)} {fps_label}", color=fps_color,
                        linestyle='--', linewidth=1.5, alpha=0.85)

    ax_cpu.set_title(title, fontsize=14, fontweight='bold')
    ax_cpu.set_xlabel("Elapsed Time (Seconds)", fontsize=11)
    ax_cpu.set_ylabel("CPU Utilization (%)", fontsize=11)
    ax_fps.set_ylabel(f"{fps_label} (Hz)", fontsize=11)
    ax_cpu.grid(True, linestyle=':', alpha=0.7)

    # Same transient-spike clipping as plot_graph, on the CPU axis only.
    top = robust_ylim_top(cpu_smoothed)
    if top is not None:
        ax_cpu.set_ylim(top=top)

    # Merge legends so solid (CPU) and dashed (FPS) entries appear together.
    h1, l1 = ax_cpu.get_legend_handles_labels()
    h2, l2 = ax_fps.get_legend_handles_labels()
    ax_cpu.legend(h1 + h2, l1 + l2, loc="upper right", fontsize=9)
    fig.tight_layout()
    fig.savefig(filename, format='svg')
    plt.close(fig)


def build_direct_averages(data):
    """Calculates pure mathematical averages for uniform high-frequency runs."""
    avg_data = defaultdict(dict)
    time_grids = {}
    
    for flavor in data.keys():
        run_nums = list(data[flavor].keys())
        if not run_nums:
            continue
            
        # Find the minimum array length across all runs for this flavor
        # (This protects against a run missing a single 50ms tick at the very end)
        min_len = min(len(data[flavor][r]['t']) for r in run_nums)
        
        # Since times are uniform, we just use the first run's timeline truncated to min_len
        time_grids[flavor] = data[flavor][run_nums[0]]['t'][:min_len]
        
        for metric in METRICS.keys():
            # Grab the arrays, truncate them to the exact same length, and stack them
            arrays = [data[flavor][r][metric][:min_len] for r in run_nums]
            stacked_data = np.vstack(arrays).astype(float)

            # NaN-aware mean — runs missing a metric (e.g. older recordings
            # without inner_fps) drop out of the column average instead of
            # poisoning the whole flavor average.
            with np.errstate(invalid='ignore'):
                avg_data[flavor][metric] = np.nanmean(stacked_data, axis=0)
                
    return time_grids, avg_data

def main():
    print("[*] Parsing JSONL files from results/...")
    data, run_numbers = parse_files()
    
    if not data:
        print("[!] No matching data found. Exiting.")
        return

    os.makedirs(f"{OUTPUT_DIR}/averages", exist_ok=True)
    os.makedirs(f"{OUTPUT_DIR}/individual", exist_ok=True)
    
    flavors = list(data.keys())
    print(f"[*] Found flavors: {flavors}")
    print(f"[*] Found runs: {run_numbers}")

    # ---------------------------------------------------------
    # 1. GENERATE AVERAGES (4 Graphs)
    # ---------------------------------------------------------
    print("[*] Calculating direct column averages for uniform runs...")
    time_grids, avg_data = build_direct_averages(data)
    
    for metric, m_info in METRICS.items():
        x_dict = {f: time_grids[f] for f in flavors if f in time_grids}
        y_dict = {f: avg_data[f].get(metric) for f in flavors if metric in avg_data.get(f, {})}

        filename = f"{OUTPUT_DIR}/averages/Avg_{m_info['title'].replace(' ', '_')}.svg"
        plot_graph(x_dict, y_dict, f"AVERAGE: {m_info['title']} ({len(run_numbers)} Runs)", m_info['ylabel'], filename)

    # CPU-vs-FPS overlay (averaged) — one chart per flavor so the dual lines
    # don't compete with two other flavor pairs on the same axes.
    avg_overlays = 0
    for flavor in flavors:
        if flavor not in time_grids: continue
        xd  = {flavor: time_grids[flavor]}
        cd  = {flavor: avg_data[flavor].get('c')}         if 'c'         in avg_data.get(flavor, {}) else {}
        ind = {flavor: avg_data[flavor].get('inner_fps')} if 'inner_fps' in avg_data.get(flavor, {}) else {}
        otd = {flavor: avg_data[flavor].get('outer_fps')} if 'outer_fps' in avg_data.get(flavor, {}) else {}
        plot_cpu_vs_fps(xd, cd, ind,
                        f"AVERAGE [{flavor_label(flavor)}]: CPU vs Sim FPS ({len(run_numbers)} Runs)",
                        f"{OUTPUT_DIR}/averages/Avg_{flavor_label(flavor)}_CPU_vs_Sim_FPS.svg", fps_label='Sim FPS')
        plot_cpu_vs_fps(xd, cd, otd,
                        f"AVERAGE [{flavor_label(flavor)}]: CPU vs Update FPS ({len(run_numbers)} Runs)",
                        f"{OUTPUT_DIR}/averages/Avg_{flavor_label(flavor)}_CPU_vs_Update_FPS.svg", fps_label='Update FPS')
        avg_overlays += 2

    print(f"[+] Generated {len(METRICS) + avg_overlays} Average Graphs.")

    # ---------------------------------------------------------
    # 2. GENERATE INDIVIDUAL RUN COMPARISONS (40 Graphs)
    # ---------------------------------------------------------
    count = 0
    for run_num in run_numbers:
        for metric, m_info in METRICS.items():
            x_dict = {}
            y_dict = {}

            for flavor in flavors:
                if run_num in data[flavor]:
                    x_dict[flavor] = data[flavor][run_num]['t']
                    y_dict[flavor] = data[flavor][run_num][metric]

            filename = f"{OUTPUT_DIR}/individual/Run_{run_num}_{m_info['title'].replace(' ', '_')}.svg"
            plot_graph(x_dict, y_dict, f"RUN {run_num}: {m_info['title']} Comparison", m_info['ylabel'], filename)
            count += 1

        # CPU-vs-FPS overlay per run — one chart per flavor (readability).
        for flavor in flavors:
            if run_num not in data[flavor]: continue
            xd  = {flavor: data[flavor][run_num]['t']}
            cd  = {flavor: data[flavor][run_num].get('c')}
            ind = {flavor: data[flavor][run_num].get('inner_fps')}
            otd = {flavor: data[flavor][run_num].get('outer_fps')}
            plot_cpu_vs_fps(xd, cd, ind,
                            f"RUN {run_num} [{flavor_label(flavor)}]: CPU vs Sim FPS",
                            f"{OUTPUT_DIR}/individual/Run_{run_num}_{flavor_label(flavor)}_CPU_vs_Sim_FPS.svg", fps_label='Sim FPS')
            plot_cpu_vs_fps(xd, cd, otd,
                            f"RUN {run_num} [{flavor_label(flavor)}]: CPU vs Update FPS",
                            f"{OUTPUT_DIR}/individual/Run_{run_num}_{flavor_label(flavor)}_CPU_vs_Update_FPS.svg", fps_label='Update FPS')
            count += 2

    print(f"[+] Generated {count} Individual Run Graphs.")
    print(f"\n[*] Complete! All {count + len(METRICS) + avg_overlays} graphs saved to the '{OUTPUT_DIR}/' folder.")

if __name__ == "__main__":
    main()