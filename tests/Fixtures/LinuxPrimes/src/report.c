#include "report.h"

#include <inttypes.h>
#include <stdio.h>
#include <stdlib.h>

void print_table(const uint64_t *primes, size_t n) {
    for (size_t i = 0; i < n; i++) {
        printf("%6" PRIu64 "%s", primes[i], (i + 1) % 10 == 0 || i + 1 == n ? "\n" : " ");
    }
}

/* Writes a one-line report to $TMPDIR (falls back to /tmp on desktop Linux). */
int save_report(uint64_t limit, uint64_t count) {
    const char *dir = getenv("TMPDIR");
    if (dir == NULL) dir = "/tmp";
    char path[1024];
    snprintf(path, sizeof path, "%s/primes-report.txt", dir);
    FILE *f = fopen(path, "w");
    if (!f) return 0;
    fprintf(f, "primes up to %" PRIu64 ": %" PRIu64 "\n", limit, count);
    fclose(f);
    printf("Report written to %s\n", path);
    return 1;
}
