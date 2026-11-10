// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <cerrno>
#include <cstdint>
#include <vector>
#include "bpf/bpf.h"

namespace buildxl::linux::ebpf {

inline bool ProgramReferencesMap(int program_fd, __u32 map_id)
{
    bpf_prog_info info = {};
    __u32 size = sizeof(info);
    if (bpf_prog_get_info_by_fd(program_fd, &info, &size) != 0 || info.nr_map_ids == 0) return false;
    std::vector<__u32> maps(info.nr_map_ids);
    info.map_ids = (__u64)(uintptr_t)maps.data();
    if (bpf_prog_get_info_by_fd(program_fd, &info, &size) != 0) return false;
    for (auto id : maps) if (id == map_id) return true;
    return false;
}

inline int ClearBpfMap(int map_fd, __u32 key_size)
{
    std::vector<char> key(key_size);
    std::vector<char> next_key(key_size);
    if (bpf_map_get_next_key(map_fd, nullptr, key.data()))
    {
        return errno == ENOENT ? 0 : -errno;
    }

    while (true)
    {
        // Fetch the successor before deletion: a missing cursor restarts hash
        // iteration, making a sparse PID map's cleanup quadratic.
        int next_result = bpf_map_get_next_key(map_fd, key.data(), next_key.data());
        if (next_result && errno != ENOENT)
        {
            return -errno;
        }

        if (bpf_map_delete_elem(map_fd, key.data()))
        {
            return -errno;
        }

        if (next_result)
        {
            return 0;
        }

        key.swap(next_key);
    }
}

}
