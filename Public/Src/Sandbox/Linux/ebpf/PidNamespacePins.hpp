// Copyright (c) Microsoft Corporation.
// Licensed under the MIT License.

#pragma once

#include <cerrno>
#include <cstdint>
#include <fcntl.h>
#include <stdexcept>
#include <string>
#include <sys/file.h>
#include <sys/stat.h>
#include <unistd.h>

namespace buildxl::linux::ebpf {

inline std::string PidNamespacePinRoot(std::string parent, uint64_t inode)
{
    if (parent.empty() || parent.front() != '/' || inode == 0) {
        throw std::invalid_argument("PID namespace pin roots require an absolute parent and nonzero inode");
    }
    while (parent.size() > 1 && parent.back() == '/') parent.pop_back();
    return parent + (parent == "/" ? "" : "/") + "buildxl/" + std::to_string(inode);
}

// Keep directory creation race-safe and refuse symlink substitutions.
inline int OpenPidNamespacePinDirectory(const std::string& parent, uint64_t inode)
{
    if (inode == 0) { errno = EINVAL; return -1; }
    int parent_fd = open(parent.c_str(), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    if (parent_fd < 0) return -1;
    if (mkdirat(parent_fd, "buildxl", 0755) != 0 && errno != EEXIST) {
        int error = errno;
        close(parent_fd);
        errno = error;
        return -1;
    }
    int buildxl_fd = openat(parent_fd, "buildxl", O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    int open_error = errno;
    close(parent_fd);
    if (buildxl_fd < 0) { errno = open_error; return -1; }
    std::string name = std::to_string(inode);
    if (mkdirat(buildxl_fd, name.c_str(), 0755) != 0 && errno != EEXIST) {
        int error = errno;
        close(buildxl_fd);
        errno = error;
        return -1;
    }
    int result = openat(buildxl_fd, name.c_str(), O_RDONLY | O_DIRECTORY | O_NOFOLLOW | O_CLOEXEC);
    int error = errno;
    close(buildxl_fd);
    errno = error;
    return result;
}

class PidNamespacePinLock
{
    int m_fd;
    bool m_locked = false;
public:
    explicit PidNamespacePinLock(int fd) : m_fd(fd) {}
    PidNamespacePinLock(const PidNamespacePinLock&) = delete;
    PidNamespacePinLock& operator=(const PidNamespacePinLock&) = delete;
    ~PidNamespacePinLock() { Unlock(); if (m_fd >= 0) close(m_fd); }
    bool Lock()
    {
        int result;
        do { result = flock(m_fd, LOCK_EX); } while (result != 0 && errno == EINTR);
        m_locked = result == 0;
        return m_locked;
    }
    void Unlock()
    {
        if (m_locked) { flock(m_fd, LOCK_UN); m_locked = false; }
    }
};

}
