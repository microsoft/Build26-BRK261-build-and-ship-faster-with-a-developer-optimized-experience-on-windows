# build2026Demo — SPGO

Sample Profile‑Guided Optimization (SPGO) is a compiler optimization technique that improves application performance by using runtime profiling data—like traditional PGO—but instead of relying on instrumented builds and synthetic runs, it gathers lightweight hardware‑sampling data directly from real production binaries. By leveraging real‑world execution patterns with near‑zero overhead, SPGO enables more accurate optimization decisions (e.g., inlining, code layout, hot paths), making it easier to achieve meaningful performance gains while simplifying adoption compared to traditional PGO.

In this tutorial, you walk through the complete SPGO workflow: build a sample app, profile it by using `xperf`, prepare the profile data, and rebuild with the profile data. When you finish, you can apply the same process to your own projects.

For more details, please refer to [Use Sample Profile Guided Optimization (SPGO) to improve C++ performance](https://aka.ms/spgo/tutorial)

---

## Prerequisites

Before you start, make sure you have the following software and hardware.

### Software

- MSVC build tools for x64/x86/ARM64 v14.51 or later—Installed through the Visual Studio Installer.
- Windows Performance Toolkit (xperf.exe)—Download the Windows Assessment and Deployment Kit (ADK) from [ADK install](https://learn.microsoft.com/en-us/windows-hardware/get-started/adk-install) that matches your OS version. When you run the ADK installer, select the **Windows Performance Toolkit** component to get `xperf`.

### Hardware requirements

For simplicity, we assume users' dev devices support Last Branch Records (LBR) performance counters, as found on most modern x64 desktop hardware, such as Intel Haswell CPUs (4th gen Core, 2013) or later; AMD Zen 4 (2022) or later; and ARM64 ARMv9.2-A (2020) or later.

If you are using VMs or some older hardware, please refer to [Use Sample Profile Guided Optimization (SPGO) to improve C++ performance](https://aka.ms/spgo/tutorial) for other profiling paths and you might need to update your workflow accordingly.

---

### Configure `perfcore.ini`

> **⚠️ Required:** Without this step, `xperf` doesn't provide the necessary profiling data. Complete this step before running `xperf`.

Open Windows Notepad as Administrator. Then open `perfcore.ini`, located at `C:\Program Files (x86)\Windows Kits\10\Windows Performance Toolkit\perfcore.ini` if you installed the WPT in the default location. Find the DLL list section and add the following entries, one per line:

```
perf_lbr.dll
perf_spt.dll
perf_hv.dll
```

Save and close `perfcore.ini`. Ensure that `xperf` is in your path.

---

## Step 1: Build and run the sample to get a baseline

Before applying SPGO, build `spgo_bench_vm` and run it to see how fast it runs. This step shows you the performance before you optimize it by using SPGO:

**Build:**

```cmd
cl /O2 /EHsc /GL /Zi spgo_bench_vm.cpp /link /debug
copy spgo_bench_vm.exe baseline.exe
```

**Run:**

```cmd
baseline.exe 30 5000000 2>&1
```

---

## Step 2: Build spgo_bench_vm with /spgo

Now build spgo_bench_vm with SPGO enabled. This step lays the groundwork to gather profiling data.

```cmd
cl /O2 /EHsc /GL /Zi spgo_bench_vm.cpp /link /debug /spgo
```

When the build finishes, you see a message like:

```
SPD spgo_bench_vm.spd not found, compiling without profile guided optimizations
```

This message appears on the first `/spgo` build. The linker creates the SPD file but it's still empty, so it doesn't apply any SPGO optimizations yet. After you run the binary, collect profile data, and convert it to SPD, you won't see this message.

**Flag explanations:**

| Flag | Purpose |
| --- | --- |
| `/Zi` | Generate complete debugging information |
| `/EHsc` | Enable C++ exception handling |
| `/GL` | Whole-program optimization — required for SPGO. Defers final optimization to link time, enabling cross-module inlining, code layout, and dead code elimination decisions. |
| `/O2` | Optimize for speed — enables aggressive inlining, loop optimization, dead code removal, and related transforms. |
| `/link /debug` | Pass `/debug` to the linker to generate debug information (`.pdb`), which xperf uses to map profiling samples to source code. |
| `/spgo` | SPGO linker flag—embeds SPGO metadata in the binary and creates an empty `spgo_bench_vm.spd` file alongside the executable. |

---

## Step 3: Profile Workload

Start `xperf` with LBR collection:

```cmd
xperf -on LOADER+PROC_THREAD+PMC_PROFILE -MinBuffers 4096 -MaxBuffers 4096 -BufferSize 4096 -pmcprofile BranchInstructionRetired -LastBranch PmcInterrupt -setProfInt BranchInstructionRetired 16384
```

**Parameter explanation:**

| Parameter | Purpose |
| --- | --- |
| `LOADER+PROC_THREAD+PMC_PROFILE` | Kernel providers: loader events (module mapping), process/thread events (execution context), and PMC profiling events |
| `-MinBuffers 4096 -MaxBuffers 4096 -BufferSize 4096` | Large ring buffers to avoid dropped samples during a full War and Peace run |
| `-pmcprofile BranchInstructionRetired` | PMC event trigger: generate a sample on every Nth retired branch instruction |
| `-LastBranch PmcInterrupt` | Enables LBR hardware recording: on each PMC interrupt, capture the hardware last-branch record stack |
| `-setProfInt BranchInstructionRetired 16384` | Sample interval: fire an interrupt every 16,384 retired branch instructions |

With `xperf` running, run `spgo_bench_vm` against War and Peace:

```cmd
spgo_bench_vm.exe 30 5000000 2>&1
```

After `spgo_bench_vm` finishes, stop `xperf` and write the trace file. Letting other processes run during profiling dilutes sample quality. For best results, close unnecessary applications before running the workload.

```cmd
xperf -stop -d spgo_bench_vm.etl
```

After stopping `xperf` (it can take a while to write out the etl file), confirm that `spgo_bench_vm.etl` was created in the current directory.

---

## Step 4: Convert the ETL file to SPT


Run `SPTAggregate.exe` to process the raw ETL trace and create an SPT profile file:

```cmd
SPTAggregate.exe /binary spgo_bench_vm.exe /etl spgo_bench_vm.etl spgo_bench_vm.spt
```

**Parameter explanation:**

| Parameter | Purpose |
| --- | --- |
| `/binary spgo_bench_vm.exe` | The binary to extract samples from. The ETL might contain samples from all processes that ran during profiling |
| `/etl spgo_bench_vm.etl` | Input ETL trace file |
| `spgo_bench_vm.spt` | Output SPT profile file |

`SPTAggregate` outputs a summary that shows how many samples it collected. This summary is your first confirmation that profiling worked.

---


## Step 5: Convert the SPT file to SPD

```cmd
SPDConvert.exe /mode:LBR spgo_bench_vm.spd spgo_bench_vm.spt
```

`/mode:LBR` tells `SPDConvert` to interpret the SPT as containing LBR branch sequence data.

After running `SPDConvert`, confirm that `spgo_bench_vm.spd` was created (or updated) in the current directory.

---

## Step 6: Rebuild spgo_bench_vm with /spdin

Rebuild `spgo_bench_vm` by using the populated SPD file. The linker reads the profile data and applies SPGO optimizations.

```cmd
cl /O2 /EHsc /GL /Zi spgo_bench_vm.cpp /link /debug /spgo /spdin:spgo_bench_vm.spd
```

**New flag (compared to Build spgo_bench_vm with /spgo):**

| Flag | Purpose |
| --- | --- |
| `/spdin:spgo_bench_vm.spd` | Provide the SPD profile data to the linker for optimization |

The command still includes `/spgo`. It generates a new SPD file alongside the optimized binary, which you can use as the starting point for subsequent profiling iterations.

---

## Step 7: Measure the results

Run `spgo_bench_vm` again and compare elapsed times.

```cmd
spgo_bench_vm.exe 30 5000000 2>&1
```

In this particular test, SPGO using the LBR method delivered approximately 20-40% reduction in elapsed time. Your results might vary with your own projects because SPGO gains depend on how well the profiling workload represents typical execution. Larger, branch-filled codebases tend to see more improvement within the 5–10% range.

| Build | Representative elapsed time |
| --- | --- |
| Baseline (`cl /Zi /EHsc /O2 /link /debug`) | *(your measurement)* |
| `/spgo` build (no profile data yet) | *(should be close to baseline)* |
| SPGO-optimized (`/spdin`) | *(should show improvement)* |

---

## Project layout

```
DemoSPGO/
├── app/
│   └── spgo_bench_vm.cpp
└── README.md
```

## Further Readings

- [Use Sample Profile Guided Optimization (SPGO) to improve C++ performance](https://aka.ms/spgo/tutorial)

## License

MIT — see [`LICENSE`](./LICENSE).
