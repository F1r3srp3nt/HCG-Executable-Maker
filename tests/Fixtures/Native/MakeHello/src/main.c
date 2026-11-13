#include <stdio.h>
#ifndef GREETING
#define GREETING "default"
#endif
int main(void) { printf("Hello from make: %s\n", GREETING); return 0; }
