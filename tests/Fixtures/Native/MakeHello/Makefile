CC ?= cc
CFLAGS ?= -O2 -Wall

.PHONY: all clean slow

all: bin/makehello

bin/makehello: src/main.c
	mkdir -p bin
	$(CC) $(CFLAGS) -DGREETING='"$(GREETING)"' -o $@ src/main.c

slow:
	sleep 60

clean:
	rm -rf bin
