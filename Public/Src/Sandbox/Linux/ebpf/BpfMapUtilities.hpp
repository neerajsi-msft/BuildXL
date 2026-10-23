// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <cerrno>
#include <vector>
#include "bpf/bpf.h"

namespace buildxl::linux::ebpf {

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
