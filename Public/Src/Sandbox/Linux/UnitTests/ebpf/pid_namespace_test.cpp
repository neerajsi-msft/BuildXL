// Copyright (c) Microsoft. All rights reserved.
// Licensed under the MIT license. See LICENSE file in the project root for full license information.

#include <stdio.h>
#include <stdlib.h>
#include <unistd.h>
#include <sched.h>
#include <sys/wait.h>
#include <sys/stat.h>
#include <pthread.h>
#include <string.h>
#include <errno.h>
#include <functional>
#include "test_utils.hpp"

/**
 * Test binary that exercises PID namespace translation in the eBPF sandbox.
 *
 * This program creates a new PID namespace, forks a child into it, and the child
 * spawns multiple threads that each perform file operations (stat on absent files).
 * The eBPF sandbox must correctly translate the PIDs of all threads back to the
 * runner's namespace.
 *
 * Each thread stats a unique absent file (<base_path>_thread_<N>) so that accesses
 * are not deduped by the native side event cache. The main thread also stats its own
 * unique file (<base_path>_main). The C# test side can then count the number of
 * distinct accesses and compare against the expected thread count + 1. It also 
 * prints to stdou the pids observed by the child to verify that it is running in 
 * a new PID namespace (should see PID 1).
 *
 * Expected arguments: <base_path> <num_threads>
 *   base_path:    a base path used to construct per-thread absent file paths
 *   num_threads:  number of additional threads to spawn in the child process
 */

#define MAX_THREADS 16

#define MAX_PATH_LEN 512

struct thread_args {
    char path[MAX_PATH_LEN];
    int thread_id;
};

void *thread_func(void *arg) {
    struct thread_args *targs = (struct thread_args *)arg;
    struct stat st;

    // Each thread stats its own unique absent file. The eBPF sandbox will observe this
    // and must translate the PID consistently regardless of which thread triggers the probe.
    // Using distinct files avoids native-side deduplication so each access is reported.
    int ret = stat(targs->path, &st);
    if (ret != 0 && errno != ENOENT) {
        fprintf(stderr, "Thread %d: stat(%s) failed unexpectedly: %s\n", targs->thread_id, targs->path, strerror(errno));
    }

    return NULL;
}

int child_main(const char *base_path, int num_threads) {
    fprintf(stdout, "Child process started with PID %d (should be 1 in new namespace)\n", getpid());

    pthread_t threads[MAX_THREADS];
    struct thread_args targs[MAX_THREADS];

    // Cap threads at MAX_THREADS
    if (num_threads > MAX_THREADS) {
        num_threads = MAX_THREADS;
    }

    // Spawn threads that each stat a unique absent file
    for (int i = 0; i < num_threads; i++) {
        snprintf(targs[i].path, MAX_PATH_LEN, "%s_thread_%d", base_path, i);
        targs[i].thread_id = i;
        if (pthread_create(&threads[i], NULL, thread_func, &targs[i]) != 0) {
            fprintf(stderr, "Failed to create thread %d: %s\n", i, strerror(errno));
            return 1;
        }
    }

    // Also do a stat from the main thread on its own unique absent file
    struct stat st;
    char main_path[MAX_PATH_LEN];
    snprintf(main_path, MAX_PATH_LEN, "%s_main", base_path);
    stat(main_path, &st);

    // Wait for all threads to complete
    for (int i = 0; i < num_threads; i++) {
        pthread_join(threads[i], NULL);
    }

    fprintf(stdout, "All %d threads completed successfully\n", num_threads);
    return 0;
}

static int InNewPidNamespace(const std::function<int()>& action) {
    if (unshare(CLONE_NEWPID) != 0) {
        fprintf(stderr, "unshare(CLONE_NEWPID): %s\n", strerror(errno));
        return 1;
    }
    pid_t child = fork();
    if (child < 0) return 1;
    if (child == 0) _exit(action());
    int status;
    return waitpid(child, &status, 0) == child && WIFEXITED(status) ? WEXITSTATUS(status) : 1;
}

// Run outside the runner namespace: pid_namespace_test --unrelated-namespaces sandbox.bpf.o.
// Load only read-only test queries; do not attach hooks or reuse a running sandbox's maps.
static int VerifyUnrelatedNamespaces(const char* object_path) {
    int ready[2], done[2];
    if (pipe(ready) || pipe(done)) return 1;
    alarm(30);
    pid_t loader = fork();
    if (loader < 0) return 1;
    if (loader == 0) {
        close(ready[0]);
        close(done[1]);
        _exit(InNewPidNamespace([&]() {
            struct bpf_object* object = bpf_object__open_file(object_path, nullptr);
            if (libbpf_get_error(object)) return 1;
            struct bpf_program* program;
            bpf_object__for_each_program(program, object) {
                const char* name = bpf_program__name(program);
                bpf_program__set_autoload(program, strcmp(name, "test_get_current_ns_pid") == 0 ||
                                                   strcmp(name, "get_current_namespace_level") == 0);
            }
            if (bpf_object__load(object)) return 1;
            int level_fd = bpf_program__fd(bpf_object__find_program_by_name(object, "get_current_namespace_level"));
            LIBBPF_OPTS(bpf_test_run_opts, level);
            if (bpf_prog_test_run_opts(level_fd, &level)) return 1;
            struct stat ns;
            if (stat("/proc/self/ns/pid", &ns)) return 1;
            __u32 key = 0, ns_level = level.retval;
            __u64 inode = ns.st_ino;
            if (bpf_map_update_elem(bpf_object__find_map_fd_by_name(object, "runner_pid_level"), &key, &ns_level, BPF_ANY) ||
                bpf_map_update_elem(bpf_object__find_map_fd_by_name(object, "runner_pid_ns_inode"), &key, &inode, BPF_ANY)) return 1;
            int query = bpf_program__fd(bpf_object__find_program_by_name(object, "test_get_current_ns_pid"));
            LIBBPF_OPTS(bpf_test_run_opts, own);
            if (bpf_prog_test_run_opts(query, &own) || own.retval != (__u32)getpid()) return 1;
            struct bpf_prog_info info = {};
            __u32 size = sizeof(info);
            if (bpf_prog_get_info_by_fd(query, &info, &size) ||
                write(ready[1], &info.id, sizeof(info.id)) != sizeof(info.id)) return 1;
            char stop;
            int result = read(done[0], &stop, 1) == 1 ? 0 : 1;
            bpf_object__close(object);
            return result;
        }));
    }
    close(ready[1]);
    close(done[0]);
    __u32 id = 0;
    int result = 1;
    if (read(ready[0], &id, sizeof(id)) == sizeof(id)) {
        pid_t sibling = fork();
        if (sibling == 0) {
            close(ready[0]);
            close(done[1]);
            _exit(InNewPidNamespace([&]() {
                int query = bpf_prog_get_fd_by_id(id);
                if (query < 0) return 1;
                LIBBPF_OPTS(bpf_test_run_opts, other);
                int error = bpf_prog_test_run_opts(query, &other);
                close(query);
                // Both namespace children have PID 1 and the same level, but only the loader is visible.
                return error == 0 && other.retval == 0 ? 0 : 1;
            }));
        }
        int status;
        if (sibling > 0 && waitpid(sibling, &status, 0) == sibling && WIFEXITED(status))
            result = WEXITSTATUS(status);
        if (write(done[1], "x", 1) != 1) result = 1;
    }
    close(ready[0]);
    close(done[1]);
    int status;
    if (waitpid(loader, &status, 0) != loader || !WIFEXITED(status) || WEXITSTATUS(status) != 0) result = 1;
    return result;
}

int main(int argc, char **argv) {
    if (argc == 3 && strcmp(argv[1], "--unrelated-namespaces") == 0)
        return VerifyUnrelatedNamespaces(argv[2]);
    if (argc == 3 && strcmp(argv[1], "--expect-current-pid") == 0) {
        int expected_pid = atoi(argv[2]);
        if (expected_pid == -1) {
            expected_pid = getpid();
        }
        int program_fd = GetTestProgramFd("test_get_current_ns_pid");
        if (program_fd < 0) {
            fprintf(stderr, "PID namespace test query is not loaded\n");
            return 1;
        }
        LIBBPF_OPTS(bpf_test_run_opts, opts);
        int error = bpf_prog_test_run_opts(program_fd, &opts);
        close(program_fd);
        if (error) {
            fprintf(stderr, "PID namespace query failed: %s\n", strerror(errno));
            return 1;
        }
        fprintf(stdout, "Runner-namespace PID: %u; caller PID: %d; expected: %d\n",
                opts.retval, getpid(), expected_pid);
        return opts.retval == (unsigned int)expected_pid ? 0 : 1;
    }

    if (argc < 3) {
        fprintf(stderr, "Usage: %s <path_to_stat> <num_threads>\n", argv[0]);
        return 1;
    }

    const char *path = argv[1];
    int num_threads = atoi(argv[2]);

    if (num_threads <= 0) {
        fprintf(stderr, "num_threads must be positive\n");
        return 1;
    }

    fprintf(stdout, "Parent PID: %d, creating new PID namespace\n", getpid());

    // Create a new PID namespace. The next fork will place the child as PID 1 in it.
    if (unshare(CLONE_NEWPID) != 0) {
        fprintf(stderr, "unshare(CLONE_NEWPID) failed: %s\n", strerror(errno));
        return 1;
    }

    pid_t pid = fork();
    if (pid < 0) {
        fprintf(stderr, "fork() failed: %s\n", strerror(errno));
        return 1;
    }

    if (pid == 0) {
        // Child: runs in the new PID namespace
        return child_main(path, num_threads);
    }

    // Parent: wait for the child
    int status;
    if (waitpid(pid, &status, 0) < 0) {
        fprintf(stderr, "waitpid failed: %s\n", strerror(errno));
        return 1;
    }

    if (WIFEXITED(status)) {
        int exit_code = WEXITSTATUS(status);
        fprintf(stdout, "Child exited with code %d\n", exit_code);
        return exit_code;
    }

    fprintf(stderr, "Child did not exit normally\n");
    return 1;
}
