#ifndef PRIMECORE_SIEVE_H
#define PRIMECORE_SIEVE_H
#include <stddef.h>
#include <stdint.h>

/* Counts primes in [2, limit] with a segmented sieve split across `threads` worker threads. */
uint64_t count_primes(uint64_t limit, int threads);
/* Writes up to `max` primes <= limit into out; returns how many were written. */
size_t first_primes(uint64_t limit, uint64_t *out, size_t max);
int is_prime(uint64_t n);
#endif
