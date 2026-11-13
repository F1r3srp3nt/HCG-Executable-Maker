#ifndef REPORT_H
#define REPORT_H
#include <stddef.h>
#include <stdint.h>
void print_table(const uint64_t *primes, size_t n);
int save_report(uint64_t limit, uint64_t count);
#endif
