#include <stdio.h>
int add(int a, int b);
int dep_value(void);
int main(void) { printf("2 + 3 = %d (dep %d)\n", add(2, 3), dep_value()); return 0; }
