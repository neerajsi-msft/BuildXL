# BuildXL Sandboxing
Process sandboxing is required by BuildXL to observe the actions of processes and, in some cases, to prevent processes from taking certain actions. Each operating system requires different approaches.

## Nomenclature
Glossary for some of the unfamiliar words you'll see in the code.

* A <i>pip</i> is a generic term, an acronym of Primitive Indivisible Processing, a unit of accounting in a build dependency graph. Examples include running real, sandboxed processes or process trees, or waiting for parallel graph nodes to complete.
* A <i>file probe</i> is a filesystem action that tests for a file's presence. Depending on the filesystem and OS, this may map to any of several actual API call patterns.
* "Domino": The internal code name for BuildXL. Its name remains in a few places in the code.

## Windows Sandboxing
For Windows sandboxing we utilize a much more refined and battle-tested version of the [Detours](https://github.com/Microsoft/Detours) codebase, forked a couple of years ago. Detours allows starting a process suspended and then hooking any desired set of Win32 APIs to provide callbacks into custom code, which can implement patterns like counting calls, tracking filesystem calls for accesses, redirecting filesystem paths, blocking access to paths, tracking registry usage, and so on.

Most of the core Detours file names from that repo appear in the Windows sandboxing codebase, but with numerous notes about changes made versus the original. We add a DetoursServices wrapper that implements many important layered enhancements atop the base Detours framework, including detouring a large number of Win32 APIs related to filesystem I/O, adding the ability to block accesses to paths, and reporting out directory enumerations, file probes (checking for file presence), file opens and closes, data reads, and data writes. We also properly handle transitions across 32-bit <-> 64-bit process boundaries.

The blocking capabilities are utilized by the BuildXL sandbox code to block access to disallowed paths, e.g. paths that are known to have been created by other pips that have not been declared as dependencies in the pip dependency graph.

The accounting capabilities are used for bookkeeping and post-execution rule enforcement.

In terms of performance, this implementation adds 1-5% of time overhead to running a process.

Technical note: The top-level process initiating Detours calls must be a 64-bit process. Detours bootstrapping code is hard-coded to start from 64-bit, matching the requirements for large memory needs for the BuildXL engine for parsing and tracking large repos.

## Linux Sandboxing

Akin to Detouring on Windows, the [dynamic loader](https://www.man7.org/linux/man-pages/man8/ld.so.8.html) allows for *library preloading* (a.k.a. function interposing) to hook various system calls.  Just like on Windows, the BuildXL sandbox leverages this feature to intercept all relevant filesystem-related system calls from the standard C library (`libc`) and handle them appropriately (e.g., report all requested accesses to the BuildXL process, block disallowed accesses, etc.).

For a full list of all interposed system calls, see [syscalls.md](/Public/Src/Sandbox/Linux/syscalls.md).

The semantics of how various high-level filesystem operations (e.g., absent probes, directory enumerations, reads, writes, etc.) are handled is expected to be the same on all supported operating systems.

A clear **limitation** of this approach is that in only applies to executables that are **dynamically linked** to `libc`.  In Linux, that is the case for the vast majority of executables.  Notable exceptions, however, are programs written in [`Go`](https://go.dev/) which are by default statically linked.

### An eBPF-based sandbox

A new Linux sandbox is being rolled out based on [eBPF](https://ebpf.io/). The plan is that it will eventually replace the interpose-based sandbox. The main goal is to overcome the limitation mentioned above, where with interpose file accesses can only be detected when executables are dynamically linked and the corresponding libc wrapper is used. [io_uring](https://en.wikipedia.org/wiki/Io_uring) is a clear example of an API that is being increasingly used by tools for which there is no libc wrapper yet, and direct syscalls are being performed. Moreover, io_uring is specifically about IO and therefore particularly disruptive for offering a reliable sandbox.

eBPF allows to trace very low level kernel-side functions, which reduces the list of functions to trace significantly. See [sandbox.bpf.c](Public\Src\Sandbox\Linux\ebpf\sandbox.bpf.c) for a full list.

#### High-level architecture

Each pip is wrapped in a bpf runner program. This program is responsible for setting up the [bpf maps](https://www.kernel.org/doc/html/latest/bpf/maps.html) for the pip in question and acts as the client side consumer of the events captured on kernel side. The bpf runner then sends the captured events through a FIFO to the managed side of BuildXL (in the same way the interpose sandbox does). The runner is also responsible for tearing down the pip.

eBPF programs are only loaded once in the lifetime of a build using an eBPF daemon, which gets launched as soon as the build starts and unloads the bpf programs when the build ends. Each bpf runner just verifies all required programs are already loaded and reuse the bpf maps that are setup by the daemon. The following diagram depicts the general flow:

#### PID namespace pin layout and lifetime

The libbpf pin parent is `/sys/fs/bpf`. BuildXL pins its maps beneath
`/sys/fs/bpf/buildxl/<pid-namespace-inode>/`, where the inode comes from
`stat("/proc/self/ns/pid")`. All pins created by the runner, including the
immutable runner-namespace identity map, use this directory. The kernel-side
namespace-inode filter remains enabled.

Different PID namespaces get separate map/program sets. Program discovery
matches both the loading-witness name and the namespace identity map ID; it
does not reuse another namespace's program merely because its name matches.
Runners in the same namespace reuse its maps and attached programs, including
across BuildXL instances. Directory creation tolerates concurrent creators,
refuses symlink substitutions, and an exclusive directory lock serializes
discovery/loading and initial map setup. The lock is released before running
the pip, so pip execution is not serialized.

Only **maps** are pinned. Programs and `bpf_link`s are not pinned: the loading
runner's skeleton owns the attachment FDs, and destroying that skeleton detaches
its programs. A reuse runner does not take ownership of those links. As before,
the loading daemon must remain alive while runners depend on its attachments;
namespace isolation does not introduce reference-counted daemon ownership.
Per-pip inner maps are removed from the shared outer maps after their process
trees finish. Pinned outer maps and the namespace directory survive runner exit
and may be reused when programs are loaded again.

There is **no heartbeat, lease, automatic stale-directory cleanup, or max-age
setting**. Map pins persist until explicitly removed, bpffs is unmounted, or the
host reboots. `wsl --shutdown` also clears the WSL VM's bpffs. Stale namespace
directories alone do not keep programs attached because links are not pinned.

After **all builds/runners using a namespace have stopped**, an administrator
can inspect and remove that namespace's pins:

```sh
# Replace INODE with the exact stale namespace inode; never run against an active build.
sudo ls -l /sys/fs/bpf/buildxl/INODE
sudo rm -f -- /sys/fs/bpf/buildxl/INODE/*
sudo rmdir -- /sys/fs/bpf/buildxl/INODE
```

Removing map pins does not forcibly detach programs held by live link FDs.
Do not delete the directory while any runner uses it: doing so can split a
namespace into incompatible live map sets.

Legacy binaries pin maps directly under `/sys/fs/bpf`. New runners neither
reuse nor remove those flat pins or globally discovered legacy programs.
After legacy builds have stopped, manually remove only the known BuildXL pins
(`pid_map`, `sandbox_options_per_pip`, `stats_per_pip`, `file_access_per_pip`,
`debug_buffer_per_pip`, `breakaway_processes_per_pip`, `event_cache_per_pip`,
`string_cache_per_pip`, `neg_dentry_cache_per_pip`, `last_path_per_pip`,
`untracked_scopes_per_pip`, and, for versions that pin it, `runner_pid_ns_inode`),
or reboot / use `wsl --shutdown`. Never blanket-delete other applications'
entries in `/sys/fs/bpf`.

The native namespace tests include privilege-free path/separation/locking tests.
Privileged separation and nested-namespace tests mount a private bpffs in a
private mount namespace beneath their test working directory; they never use
the host's pin root. They are gated by the existing eBPF-for-tests setting and
print a capability skip when unavailable.

<div style="text-align: center;">

![eBPF architecture](../Wiki/ebpf-architecture.png)

</div>

Each pip is given a dedicated ring buffer to communicate file accesses to user side. A global bpf map tracks which pids need tracking and the pip id that contains that process. A second map contains the ring buffer that corresponds to each pip id. eBPF programs on kernel side lookup the ring buffer where they should place the traced access based on the captured pid, and on user side, each bpf runner registers to listen to a particular ring buffer.

<div style="text-align: center;">

![eBPF ring buffers](../Wiki/ebpf-ring-buffers.png)

</div>

## Sandbox Demos
See the [Demos](../../Public/Src/Demos/Demos.md) page which includes sandbox projects to help understand how sandboxing works.
