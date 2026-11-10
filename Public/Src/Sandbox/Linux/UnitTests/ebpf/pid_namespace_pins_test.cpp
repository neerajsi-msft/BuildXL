// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#include "../../ebpf/PidNamespacePins.hpp"
#include <cstdio>
#include <filesystem>
#include <thread>
#include <vector>
#include <atomic>

using namespace buildxl::linux::ebpf;

int main()
{
    auto check = [](bool condition, const char* message) {
        if (!condition) { fprintf(stderr, "%s\n", message); std::exit(1); }
    };
    check(PidNamespacePinRoot("/sys/fs/bpf", 123) == "/sys/fs/bpf/buildxl/123", "default root");
    check(PidNamespacePinRoot("/private/root///", 4294967297ULL) == "/private/root/buildxl/4294967297", "private root and full inode");
    check(PidNamespacePinRoot("/", 1) == "/buildxl/1", "filesystem root");
    check(PidNamespacePinRoot("/private/root", 1) != PidNamespacePinRoot("/private/root", 2), "namespace separation");
    for (auto parent : {"", "relative"}) {
        bool rejected = false;
        try { PidNamespacePinRoot(parent, 1); } catch (const std::invalid_argument&) { rejected = true; }
        check(rejected, "reject nonabsolute root");
    }
    bool rejected = false;
    try { PidNamespacePinRoot("/root", 0); } catch (const std::invalid_argument&) { rejected = true; }
    check(rejected, "reject zero inode");

    // Ordinary project-local filesystem only: no BPF syscalls or namespace privileges.
    auto parent = std::filesystem::current_path() / "pins-unit";
    std::filesystem::create_directory(parent);
    std::atomic<int> active{0}, errors{0};
    std::vector<std::thread> threads;
    for (int i = 0; i < 8; ++i) threads.emplace_back([&]() {
        int fd = OpenPidNamespacePinDirectory(parent.string(), 42);
        if (fd < 0) { errors++; return; }
        PidNamespacePinLock lock(fd);
        if (!lock.Lock()) { errors++; return; }
        if (++active != 1) errors++;
        std::this_thread::yield();
        --active;
    });
    for (auto& thread : threads) thread.join();
    check(errors == 0, "concurrent initialization lock");
    auto unsafe = parent / "buildxl" / "43";
    std::filesystem::create_directory(parent / "outside");
    std::filesystem::create_directory_symlink(parent / "outside", unsafe);
    check(OpenPidNamespacePinDirectory(parent.string(), 43) == -1, "reject namespace symlink");
    std::filesystem::remove_all(parent);
    fprintf(stdout, "PID namespace path, separation and startup-lock tests passed\n");
    return 0;
}
