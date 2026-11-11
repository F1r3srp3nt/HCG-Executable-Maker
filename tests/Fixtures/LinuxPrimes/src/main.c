/* primes — count and list prime numbers (a small Linux command-line program). */
#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <unistd.h>

#include "report.h"
#include "sieve.h"

#ifdef __GLIBC__
#include <gnu/libc-version.h>
#endif

static void usage(void) {
    fprintf(stderr, "usage: primes [-n LIMIT] [-t THREADS] [-l] [-i]\n"
                    "  -n LIMIT    count primes up to LIMIT (default 1000000)\n"
                    "  -t THREADS  worker threads (default 4)\n"
                    "  -l          list the first 30 primes\n"
                    "  -i          interactive: read numbers from standard input and test them\n");
}

int main(int argc, char **argv) {
    uint64_t limit = 1000000;
    int threads = 4, list = 0, interactive = 0, opt;
    while ((opt = getopt(argc, argv, "n:t:lih")) != -1) {
        switch (opt) {
        case 'n': limit = strtoull(optarg, NULL, 10); break;
        case 't': threads = atoi(optarg); break;
        case 'l': list = 1; break;
        case 'i': interactive = 1; break;
        default: usage(); exit(2);
        }
    }
    printf("primes 1.0 — %ld CPU(s) online", sysconf(_SC_NPROCESSORS_ONLN));
#ifdef __GLIBC__
    printf(", glibc %s", gnu_get_libc_version());
#endif
    printf("\n");

    if (list) {
        uint64_t first[30];
        size_t n = first_primes(limit, first, 30);
        print_table(first, n);
    }
    uint64_t count = count_primes(limit, threads);
    printf("There are %" PRIu64 " primes up to %" PRIu64 " (%d thread(s)).\n", count, limit, threads);
    save_report(limit, count);

    if (interactive) {
        char line[128];
        printf("Enter numbers to test, one per line (end of input to stop):\n");
        fflush(stdout);
        while (fgets(line, sizeof line, stdin)) {
            uint64_t v = strtoull(line, NULL, 10);
            printf("%" PRIu64 " is %s\n", v, is_prime(v) ? "prime" : "not prime");
            fflush(stdout);
        }
    }
    return 0;
}
