#include "sieve.h"

#include <math.h>
#include <pthread.h>
#include <stdlib.h>
#include <string.h>

int is_prime(uint64_t n) {
    if (n < 2) return 0;
    if (n % 2 == 0) return n == 2;
    for (uint64_t d = 3; d * d <= n; d += 2)
        if (n % d == 0) return 0;
    return 1;
}

struct segment { uint64_t lo, hi, count; const unsigned char *base; uint64_t root; };

static void *count_segment(void *arg) {
    struct segment *s = arg;
    size_t len = (size_t)(s->hi - s->lo + 1);
    unsigned char *composite = calloc(len, 1);
    if (!composite) return NULL;
    for (uint64_t p = 2; p <= s->root; p++) {
        if (s->base[p]) continue;
        uint64_t start = p * p > s->lo ? p * p : ((s->lo + p - 1) / p) * p;
        for (uint64_t m = start; m <= s->hi; m += p) composite[m - s->lo] = 1;
    }
    for (size_t i = 0; i < len; i++)
        if (!composite[i] && s->lo + i >= 2) s->count++;
    free(composite);
    return NULL;
}

uint64_t count_primes(uint64_t limit, int threads) {
    if (limit < 2) return 0;
    if (threads < 1) threads = 1;
    uint64_t root = (uint64_t)sqrt((double)limit) + 1;
    unsigned char *base = calloc((size_t)root + 1, 1);
    if (!base) return 0;
    for (uint64_t p = 2; p * p <= root; p++)
        if (!base[p]) for (uint64_t m = p * p; m <= root; m += p) base[m] = 1;
    pthread_t *ids = calloc((size_t)threads, sizeof *ids);
    struct segment *segs = calloc((size_t)threads, sizeof *segs);
    uint64_t step = limit / (uint64_t)threads + 1;
    for (int t = 0; t < threads; t++) {
        segs[t].lo = (uint64_t)t * step;
        segs[t].hi = segs[t].lo + step - 1 > limit ? limit : segs[t].lo + step - 1;
        segs[t].base = base;
        segs[t].root = root;
        pthread_create(&ids[t], NULL, count_segment, &segs[t]);
    }
    uint64_t total = 0;
    for (int t = 0; t < threads; t++) {
        pthread_join(ids[t], NULL);
        total += segs[t].count;
    }
    free(ids);
    free(segs);
    free(base);
    return total;
}

size_t first_primes(uint64_t limit, uint64_t *out, size_t max) {
    size_t n = 0;
    for (uint64_t i = 2; i <= limit && n < max; i++)
        if (is_prime(i)) out[n++] = i;
    return n;
}
